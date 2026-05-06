using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using Multiplayer.Common;
using Multiplayer.Common.Util;
using RimWorld;
using UnityEngine;
using Verse;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Multiplayer.Client.Desyncs;

public class SaveableDesyncInfo(
    SyncCoordinator coordinator,
    ClientSyncOpinion local,
    ClientSyncOpinion remote,
    int diffAt,
    bool diffAtFound)
{
    public readonly ClientSyncOpinion local = local;
    public readonly ClientSyncOpinion remote = remote;
    public readonly int diffAt = diffAt;
    private readonly Task<string> metadata = Task.Run(MetadataGenerator.Generate);
    private readonly Task<FileInfo> replay = Task.Run(SaveReplayIfApplicable);

    public bool ReadyToSave => metadata.IsCompleted && replay.IsCompleted;

    public void Save([CanBeNull] HostInfo hostInfo)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            var desyncFilePath = FindFileNameForNextDesyncFile();
            using var zip = MpZipFile.Open(Path.Combine(Multiplayer.DesyncsDir, desyncFilePath + ".zip"), ZipArchiveMode.Create);

            zip.AddEntry("desync_info", GetDesyncDetails());

            zip.AddEntry("local_traces.txt", GetLocalTraces());
            zip.AddEntry("host_traces.txt", hostInfo?.Traces ?? "No host traces");

            zip.AddEntry("local_jitted_methods.txt", JittedMethods.GetJittedMethodsString());
            zip.AddEntry("host_jitted_methods.txt", hostInfo?.JittedMethods ?? "No host jitted methods");

            var extraLogs = LogGenerator.PrepareLogData();
            if (extraLogs != null) zip.AddEntry("local_logs.txt", extraLogs);

            zip.AddEntry("local_metadata.txt", metadata.Result);

            // Section 8 paired save. Local: a fresh save of the desynced client's current state,
            // produced inline because we're already on the main thread (Save runs from
            // DesyncedWindow.WindowUpdate). Heavy diagnostic mode also runs the save twice and
            // diffs the bytes — see WriteHeavyDiagnostic. Host: the cached worldData snapshot
            // shipped by the server in response to Client_RequestHostSave; potentially stale
            // (last join-point upload) but a meaningful reference for post-mortem diff.
            try
            {
                var localSave = TrySaveLocalGameToBytes();
                if (localSave != null)
                    zip.AddEntry("local_save.rws", localSave);
            }
            catch (Exception e)
            {
                Log.Error($"Failed to write local save into desync zip: {e}");
            }

            if (hostInfo?.HostSavedGame is { Length: > 0 } host)
                zip.AddEntry("host_save.rws", host);
            else
                zip.AddEntry("host_save.txt", "No host save received (server had no cached worldData snapshot, or transfer timed out).");

            if (Multiplayer.settings.heavyDiagnosticMode &&
                (Prefs.DevMode || MpVersion.IsDebug))
            {
                try
                {
                    WriteHeavyDiagnostic(zip);
                }
                catch (Exception e)
                {
                    Log.Error($"Heavy desync diagnostic threw: {e}");
                }
            }

            try
            {
                replay.Wait();
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions.SingleOrDefault(inner => inner is TaskCanceledException) == null) throw;
            }
            if (replay.IsCompletedSuccessfully) {
                var replayFile = replay.Result;
                zip.CreateEntryFromFile(replayFile.FullName, "replay.rwmts", CompressionLevel.NoCompression);
                DeleteFileSilent(replayFile);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Exception writing desync info: {e}");
        }

        Log.Message($"Desync info writing took {watch.ElapsedMilliseconds}");
    }

    // Save the current game to a gzipped XML byte array, mirroring the host's wire format
    // (worldData.savedGame is also gzipped XML). Both sides written into the desync zip use the
    // same format so consumers can decompress and diff identically. Returns null on failure
    // rather than throwing so the rest of the report still writes.
    private static byte[]? TrySaveLocalGameToBytes()
    {
        try
        {
            var doc = SaveLoad.SaveGameToDoc();
            using var mem = new MemoryStream();
            using (var gzip = new GZipStream(mem, CompressionLevel.Optimal, leaveOpen: true))
                doc.Save(gzip);
            return mem.ToArray();
        }
        catch (Exception e)
        {
            Log.Error($"SaveGameToDoc threw during desync report: {e}");
            return null;
        }
    }

    // Heavy diagnostic: save twice in a row and byte-diff. A diff means the save XML's content
    // depends on something non-deterministic (Dictionary/HashSet enumeration order, object hash
    // codes, GC-ordered GUIDs, etc.) — a strong signal for the underlying determinism bug. We
    // do NOT reload between saves: reloading from inside a desync handler is a deadlock minefield
    // (long-event handler + main-thread + window-stack-clearing all interacting). Save-twice is
    // a strict subset of the ideal save-reload-save check but catches the most common class.
    // TODO(roadmap section 8.3): once the rejoin pipeline is decoupled enough to be safe inside
    // a desync handler, extend this to actually reload between the two saves and diff the
    // resulting byte streams — that would also catch Load+Save asymmetry, not just Save+Save.
    private static void WriteHeavyDiagnostic(ZipArchive zip)
    {
        var first = TrySaveLocalGameToBytes();
        var second = TrySaveLocalGameToBytes();
        if (first == null || second == null)
        {
            zip.AddEntry("heavy_diagnostic.txt", "Heavy diagnostic: one of the two saves failed; cannot compare.");
            return;
        }

        var equal = first.Length == second.Length && BytesEqual(first, second);
        var sb = new StringBuilder();
        sb.AppendLine("###Heavy desync diagnostic###");
        sb.AppendLine($"Save 1 size: {first.Length}");
        sb.AppendLine($"Save 2 size: {second.Length}");
        sb.AppendLine($"Bytes equal: {equal}");
        if (!equal)
            sb.AppendLine("Save+Save NOT idempotent. Likely determinism bug: a data structure being scribed uses non-deterministic enumeration order (Dictionary/HashSet without explicit sort), runtime hash codes, or GC-ordered ids. Compare the two byte streams to localise the diverging section.");

        zip.AddEntry("heavy_diagnostic.txt", sb.ToString());
        if (!equal)
        {
            zip.AddEntry("heavy_diagnostic_save1.rws", first);
            zip.AddEntry("heavy_diagnostic_save2.rws", second);
        }
    }

    private string GetLocalTraces()
    {
        var traceMessage = "";
        var localCount = local.desyncStackTraceHashes.Count;
        var remoteCount = remote.desyncStackTraceHashes.Count;
        int count = Math.Min(localCount, remoteCount);

        if (diffAt == -1)
        {
            if (count == 0)
            {
                return $"No traces (remote: {remoteCount}, local: {localCount})";
            }

            traceMessage = "Note: trace hashes are equal between local and remote\n\n";
        }
        else if (!diffAtFound)
        {
            traceMessage = "Note: traces differ in amount, but the existing ones are equal. This means that a tick" +
                           " has ended sooner on one of the connection sides\n\n";
        }

        traceMessage += local.GetFormattedStackTracesForRange(diffAt);
        return traceMessage;
    }

    private string GetDesyncDetails()
    {
        var desyncInfo = new StringBuilder();

        desyncInfo
            .AppendLine("###Tick Data###")
            .AppendLine($"Arbiter Connected And Playing|||{Multiplayer.session.ArbiterPlaying}")
            .AppendLine($"Last Valid Tick - Local|||{coordinator.lastValidTick}")
            .AppendLine($"Last Valid Tick - Arbiter|||{coordinator.arbiterWasPlayingOnLastValidTick}")
            .AppendLine("\n###Version Data###")
            .AppendLine($"Multiplayer Mod Version|||{MpVersion.Version}")
            .AppendLine($"Rimworld Version and Rev|||{VersionControl.CurrentVersionStringWithRev}")
            .AppendLine("\n###Debug Options###")
            .AppendLine($"Multiplayer Debug Build - Client|||{MpVersion.IsDebug}")
            .AppendLine($"Multiplayer Debug Mode - Host|||{Multiplayer.GameComp.debugMode}")
            .AppendLine($"Rimworld Developer Mode - Client|||{Prefs.DevMode}")
            .AppendLine("\n###Server Info###")
            .AppendLine($"Player Count|||{Multiplayer.session.players.Count}")
            .AppendLine($"Async time active|||{Multiplayer.GameComp.asyncTime}")
            .AppendLine($"Multifaction active|||{Multiplayer.GameComp.multifaction}")
            .AppendLine($"Map Count|||{Find.Maps?.Count.ToStringSafe()}")
            .AppendLine("\n###Canonical Fingerprint###")
            .AppendLine($"Local|||0x{local.canonicalFingerprint:X16}")
            .AppendLine($"Remote|||0x{remote.canonicalFingerprint:X16}");

        // Per-component breakdown — only meaningful when the local opinion is the LOCAL
        // client; otherwise the values come from a remote opinion that didn't compute
        // detail. We deliberately only walk the local side because ComputeDetailed reads
        // live game state which only exists on this peer. Remote opinions only carry the
        // single combined ulong on the wire (SyncOpinion.canonicalFingerprint), so no per-
        // component breakdown crosses the network. Reader hint: if local fingerprint
        // matches remote, the diff is in the deeper RNG/trace checks below.
        if (local.isLocalClientsOpinion)
        {
            desyncInfo.AppendLine("\n###Canonical components (local only - remote ships totals only)###");
            foreach (var kv in CanonicalFingerprint.ComputeDetailed())
                desyncInfo.AppendLine($"local.{kv.Key}|||0x{kv.Value:X16}");
        }

        desyncInfo
            .AppendLine("\n###CPU Info###")
            .AppendLine($"Processor Name|||{SystemInfo.processorType}")
            .AppendLine($"Processor Speed (MHz)|||{SystemInfo.processorFrequency}")
            .AppendLine($"Thread Count|||{SystemInfo.processorCount}")
            .AppendLine("\n###GPU Info###")
            .AppendLine($"GPU Family|||{SystemInfo.graphicsDeviceVendor}")
            .AppendLine($"GPU Type|||{SystemInfo.graphicsDeviceType}")
            .AppendLine($"GPU Name|||{SystemInfo.graphicsDeviceName}")
            .AppendLine($"GPU VRAM|||{SystemInfo.graphicsMemorySize}")
            .AppendLine("\n###RAM Info###")
            .AppendLine($"Physical Memory Present|||{SystemInfo.systemMemorySize}")
            .AppendLine("\n###OS Info###")
            .AppendLine($"OS Type|||{SystemInfo.operatingSystemFamily}")
            .AppendLine($"OS Name and Version|||{SystemInfo.operatingSystem}");

        return desyncInfo.ToString();
    }

    private static string FindFileNameForNextDesyncFile()
    {
        const string FilePrefix = "Desync-";
        const string FileExtension = ".zip";

        //Find all current existing desync zips
        var files = new DirectoryInfo(Multiplayer.DesyncsDir).GetFiles($"{FilePrefix}*{FileExtension}");

        const int MaxFiles = 10;

        //Delete any pushing us over the limit, and reserve room for one more
        if (files.Length > MaxFiles - 1)
            files.OrderByDescending(f => f.LastWriteTime).Skip(MaxFiles - 1).Do(DeleteFileSilent);

        //Find the current max desync number
        int max = 0;
        foreach (var f in files)
            if (int.TryParse(
                    f.Name.Substring(FilePrefix.Length, f.Name.Length - FilePrefix.Length - FileExtension.Length),
                    out int result) && result > max)
                max = result;

        return $"{FilePrefix}{max + 1:00}";
    }

    private static Task<FileInfo> SaveReplayIfApplicable()
    {
        if (!Multiplayer.settings.includeReplayInDesync) return Task.FromCanceled<FileInfo>(new CancellationToken(true));

        try
        {
            var tmp = new FileInfo(Path.Combine(Multiplayer.DesyncsDir, "desync-replay.tmp.zip"));
            if (tmp.Exists) tmp.Delete();
            Replay.ForSaving(tmp).WriteData(Multiplayer.session.dataSnapshot);
            return Task.FromResult(tmp);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save replay for desync: {e}");
            return Task.FromException<FileInfo>(e);
        }
    }

    // Hand-rolled rather than .AsSpan().SequenceEqual to avoid an ambiguous-overload error: the
    // Client project targets net48 with Harmony's Span<T> shim AND System.Memory's MemoryExtensions
    // both surfacing AsSpan<T>(T[]) — pick one of them and the compiler refuses. Plain loop is fine
    // for this code path: heavy diagnostic mode is off by default and the saves are at most a few
    // MB; a couple-million-iteration loop in a desync handler is irrelevant.
    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static void DeleteFileSilent(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
        }
    }

    // Section 8: Traces/JittedMethods come from the host's response to ServerTracesPacket.Request;
    // HostSavedGame is the cached worldData snapshot fetched separately via Client_RequestHostSave.
    // Either side may be null — the Save path falls back to a "no host data" marker so the report
    // still produces.
    public record HostInfo(
        [CanBeNull] string Traces,
        [CanBeNull] string JittedMethods,
        [CanBeNull] byte[] HostSavedGame = null);
}
