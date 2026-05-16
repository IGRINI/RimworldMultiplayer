using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
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
    public int lastJoinPointAtTick = -1;

    public List<byte[]> syncInfos = new();

    public StandaloneWorldSnapshotState standaloneWorldSnapshot = new();
    public Dictionary<int, StandaloneMapSnapshotState> standaloneMapSnapshots = new();

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
        // No real save -> nothing to normalise. Tests/initial state may set savedGame to an empty
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
                return compressed;
            node.ParentNode?.RemoveChild(node);

            using var memOut = new MemoryStream();
            using (var gzipOut = new GZipStream(memOut, CompressionMode.Compress, leaveOpen: true))
                doc.Save(gzipOut);
            return memOut.ToArray();
        }
        catch (Exception e)
        {
            ServerLog.Error($"Failed to normalise savedGame for streaming join: {e.Message}; shipping savedGame as-is.");
            return compressed;
        }
    }

    private int CurrentJoinPointTick => Server.IsStandaloneServer ? Server.gameTimer : Server.workTicks;

    public bool TryStartJoinPointCreation(bool force = false, ServerPlayer? sourcePlayer = null)
    {
        int currentTick = CurrentJoinPointTick;

        if (!force && lastJoinPointAtTick >= 0 && currentTick - lastJoinPointAtTick < 30)
        {
            ServerLog.Detail($"Join point skipped: cooldown active at tick={currentTick}, last={lastJoinPointAtTick}, standalone={Server.IsStandaloneServer}");
            return false;
        }

        if (CreatingJoinPoint)
        {
            ServerLog.Detail("Join point skipped: already creating one");
            return false;
        }

        var issuingPlayer = sourcePlayer;
        if (Server.IsStandaloneServer && issuingPlayer == null)
        {
            issuingPlayer = Server.PlayingPlayers.FirstOrDefault(player => player.IsHost)
                ?? Server.PlayingPlayers.FirstOrDefault();

            if (issuingPlayer == null)
            {
                ServerLog.Detail("Join point skipped: no playing player available for standalone creation");
                return false;
            }
        }

        ServerLog.Detail($"Join point started at tick={currentTick}, force={force}, standalone={Server.IsStandaloneServer}");
        Server.SendChat("Creating a join point...");

        Server.commands.Send(CommandType.CreateJoinPoint, ScheduledCommand.NoFaction, ScheduledCommand.Global, Array.Empty<byte>(),
            sourcePlayer: Server.IsStandaloneServer ? issuingPlayer : null);
        tmpMapCmds = new Dictionary<int, List<byte[]>>();
        dataSource = new TaskCompletionSource<WorldData>();

        return true;
    }

    public void EndJoinPointCreation()
    {
        int currentTick = CurrentJoinPointTick;
        ServerLog.Detail($"Join point completed at tick={currentTick}, standalone={Server.IsStandaloneServer}");
        mapCmds = tmpMapCmds!;
        tmpMapCmds = null;
        lastJoinPointAtTick = currentTick;

        if (Server.IsStandaloneServer && Server.persistence != null)
        {
            try
            {
                Server.persistence.WriteJoinPoint(this, currentTick);
            }
            catch (Exception e)
            {
                ServerLog.Error($"Failed to persist standalone join point at tick={currentTick}: {e}");
            }
        }

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

    public bool TryAcceptStandaloneWorldSnapshot(ServerPlayer player, int tick, byte[] worldSnapshot,
        byte[] sessionSnapshot, byte[] expectedHash)
    {
        if (tick < standaloneWorldSnapshot.tick)
            return false;

        var actualHash = ComputeHash(worldSnapshot, sessionSnapshot);
        if (expectedHash.Length > 0 && !actualHash.AsSpan().SequenceEqual(expectedHash))
            return false;

        savedGame = worldSnapshot;
        sessionData = sessionSnapshot;
        standaloneWorldSnapshot = new StandaloneWorldSnapshotState
        {
            tick = tick,
            producerPlayerId = player.id,
            producerUsername = player.Username,
            sha256Hash = actualHash
        };

        Server.persistence?.WriteWorldSnapshot(worldSnapshot, sessionSnapshot, tick);

        return true;
    }

    public bool TryAcceptStandaloneMapSnapshot(ServerPlayer player, int mapId, int tick,
        byte[] mapSnapshot, byte[] expectedHash)
    {
        if (mapId < 0)
            return false;

        var snapshotState = standaloneMapSnapshots.GetOrAddNew(mapId);
        if (tick < snapshotState.tick)
            return false;

        var actualHash = ComputeHash(mapSnapshot);
        if (expectedHash.Length > 0 && !actualHash.AsSpan().SequenceEqual(expectedHash))
            return false;

        mapData[mapId] = mapSnapshot;
        snapshotState.tick = tick;
        snapshotState.producerPlayerId = player.id;
        snapshotState.producerUsername = player.Username;
        snapshotState.sha256Hash = actualHash;
        standaloneMapSnapshots[mapId] = snapshotState;

        Server.persistence?.WriteMapSnapshot(mapId, mapSnapshot);

        return true;
    }

    private static byte[] ComputeHash(params byte[][] payloads)
    {
        using var hasher = SHA256.Create();
        foreach (var payload in payloads)
        {
            hasher.TransformBlock(payload, 0, payload.Length, null, 0);
        }

        hasher.TransformFinalBlock([], 0, 0);
        return hasher.Hash;
    }
}

public struct StandaloneWorldSnapshotState
{
    public StandaloneWorldSnapshotState() { }
    public int tick;
    public int producerPlayerId;
    public string producerUsername = "";
    public byte[] sha256Hash = Array.Empty<byte>();
}

public struct StandaloneMapSnapshotState
{
    public StandaloneMapSnapshotState() { }
    public int tick;
    public int producerPlayerId;
    public string producerUsername = "";
    public byte[] sha256Hash = Array.Empty<byte>();
}
