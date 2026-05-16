using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Xml;

namespace Multiplayer.Common;

public class WorldData
{
    public int hostFactionId;
    public int spectatorFactionId;
    public byte[]? savedGame; // Compressed game save
    public byte[]? sessionData; // Compressed semi persistent data
    public Dictionary<int, byte[]> mapData = new(); // Map id to compressed map data

    public Dictionary<int, List<byte[]>> mapCmds = new(); // Map id to serialized cmds list
    public Dictionary<int, List<byte[]>>? tmpMapCmds;
    public int lastJoinPointAtWorkTicks = -1;

    public List<byte[]> syncInfos = new();

    private TaskCompletionSource<WorldData>? dataSource;

    public bool CreatingJoinPoint => tmpMapCmds != null;

    public MultiplayerServer Server { get; }

    public WorldData(MultiplayerServer server)
    {
        Server = server;
    }

    // Cached normalised savedGame for streaming initial join: same compressed XML but with the
    // <currentMapIndex> element removed, so when the client loads the world without any map data
    // RimWorld's Scribe defaults currentMapIndex to -1 (world view). Keyed by the savedGame array
    // reference to invalidate on the next host upload.
    private byte[]? cachedSavedGameForStreaming;
    private byte[]? cachedSavedGameSource;

    public byte[]? GetSavedGameForStreaming()
    {
        var src = savedGame;
        if (src == null) return null;
        if (ReferenceEquals(src, cachedSavedGameSource) && cachedSavedGameForStreaming != null)
            return cachedSavedGameForStreaming;

        cachedSavedGameForStreaming = NormaliseSavedGameForStreaming(src);
        cachedSavedGameSource = src;
        return cachedSavedGameForStreaming;
    }

    private static byte[] NormaliseSavedGameForStreaming(byte[] compressed)
    {
        // No real save → nothing to normalise. Tests/initial state may set savedGame to an empty
        // array; production host always uploads a real GZipped XML.
        if (compressed.Length == 0) return compressed;

        try
        {
            var doc = new XmlDocument { PreserveWhitespace = false };
            using (var memIn = new MemoryStream(compressed, writable: false))
            using (var gzipIn = new GZipStream(memIn, CompressionMode.Decompress))
                doc.Load(gzipIn);

            // Find ./game/currentMapIndex relative to the document root. Selector is loose to
            // tolerate either the historical "savegame" root or any other RimWorld save wrapper.
            var node = doc.SelectSingleNode("//game/currentMapIndex");
            if (node == null)
                return compressed; // Already normalised or save format unexpected — ship as-is.
            node.ParentNode?.RemoveChild(node);

            using var memOut = new MemoryStream();
            using (var gzipOut = new GZipStream(memOut, CompressionMode.Compress, leaveOpen: true))
                doc.Save(gzipOut);
            return memOut.ToArray();
        }
        catch (Exception e)
        {
            // Malformed input (not GZip, not XML, etc.). Fall back to original so the server stays
            // up; the client will fail its own load and either retry or desync.
            ServerLog.Error($"Failed to normalise savedGame for streaming join: {e.Message}; shipping savedGame as-is.");
            return compressed;
        }
    }

    public bool TryStartJoinPointCreation(bool force = false)
    {
        if (!force && Server.workTicks - lastJoinPointAtWorkTicks < 30)
            return false;

        if (CreatingJoinPoint)
            return false;

        Server.SendChat("Creating a join point...");

        Server.commands.Send(CommandType.CreateJoinPoint, ScheduledCommand.NoFaction, ScheduledCommand.Global, Array.Empty<byte>());
        tmpMapCmds = new Dictionary<int, List<byte[]>>();
        dataSource = new TaskCompletionSource<WorldData>();

        return true;
    }

    public void EndJoinPointCreation()
    {
        mapCmds = tmpMapCmds!;
        tmpMapCmds = null;
        lastJoinPointAtWorkTicks = Server.workTicks;
        dataSource!.SetResult(this);
        dataSource = null;
    }

    public void AbortJoinPointCreation()
    {
        if (!CreatingJoinPoint)
            return;

        tmpMapCmds = null;
        dataSource?.SetResult(this);
        dataSource = null;
    }

    public Task<WorldData> WaitJoinPoint()
    {
        return dataSource?.Task ?? Task.FromResult(this);
    }
}
