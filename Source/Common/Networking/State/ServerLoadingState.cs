using System.Collections.Generic;
using System.Threading.Tasks;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common;

public class ServerLoadingState : AsyncConnectionState
{
    public ServerLoadingState(ConnectionBase connection) : base(connection)
    {
    }

    [TypedPacketHandler]
    public void HandleClientKeepAlive(ClientKeepAlivePacket packet)
    {
        Player.ticksBehind = packet.ticksBehind;
        Player.ticksBehindReceivedAt = Server.gameTimer;
        Player.simulating = packet.simulating;
        Player.keepAliveAt = Server.NetTimer;

        if (RequireHost())
            Server.workTicks = packet.workTicks;

        var idMatched = Player.keepAliveId == packet.id;
        connection.OnKeepAliveArrived(idMatched);
        if (idMatched)
            Player.keepAliveId++;
    }

    protected override async Task RunState()
    {
        await Server.worldData.WaitJoinPoint();
        await EndIfDead();

        SendWorldData();

        Player.SendPlayerList();
        connection.ChangeState(ConnectionStateEnum.ServerPlaying);
    }

    public void SendWorldData()
    {
        connection.Send(Packets.Server_WorldDataStart);

        ByteWriter writer = new ByteWriter();

        // Snapshot SentCmds atomically with the payload write. The client uses this value to
        // initialise its receivedCmds; for streaming we must seed the per-player counter to the
        // SAME baseline, otherwise the first ServerTimeControlPacket carries a stale-low count
        // and ProcessTimeControl on the client (receivedCmds >= remoteSentCmds) stops gating
        // simulation.
        int sentCmdsSnapshot = Server.commands.SentCmds;
        if (Server.IsStandaloneServer)
            Player.sentCmdsCount = sentCmdsSnapshot;

        // Clear any per-mapId transfer generations from a prior session on this player. Mirrors
        // the streaming-state reset in HandleRejoin: after this point any acks referencing an old
        // generation are silently dropped by HandleMapLoaded, since the new generation starts at 1
        // on the first SendMapResponse after the (re)join.
        Player.mapTransferIds.Clear();

        writer.WriteInt32(Player.FactionId);
        writer.WriteInt32(Server.gameTimer);
        writer.WriteInt32(sentCmdsSnapshot);
        writer.WriteBool(Server.freezeManager.Frozen);
        // For streaming, ship a copy of savedGame with <currentMapIndex> stripped: with mapDataCount=0
        // a positive currentMapIndex would point at a missing map. Removing the node lets RimWorld's
        // Scribe default it to -1 (world view) cleanly. The cached normalisation invalidates whenever
        // savedGame is replaced (host re-uploads a join point).
        var savedGameForJoin = Server.IsStandaloneServer
            ? Server.worldData.GetSavedGameForStreaming()
            : Server.worldData.savedGame;
        writer.WritePrefixedBytes(savedGameForJoin);
        writer.WritePrefixedBytes(Server.worldData.sessionData);

        // When streaming is on the joining client doesn't get any maps in this initial payload — they
        // arrive lazily via Server_MapResponse triggered by PlayerCount transitions. Per-map mapCmds
        // ship together with each MapResponse, so here we only emit global cmds (mapId == ScheduledCommand.Global == -1).
        // When streaming is off, ship everything (legacy embedded host path).
        bool streaming = Server.IsStandaloneServer;

        if (streaming)
        {
            int globalEntries = 0;
            foreach (var kv in Server.worldData.mapCmds)
                if (kv.Key < 0)
                    globalEntries++;

            writer.WriteInt32(globalEntries);
            foreach (var kv in Server.worldData.mapCmds)
            {
                if (kv.Key >= 0) continue; // Skip per-map cmds; they ride with MapResponse.
                writer.WriteInt32(kv.Key);
                writer.WriteInt32(kv.Value.Count);
                foreach (var arr in kv.Value)
                    writer.WritePrefixedBytes(arr);
            }
        }
        else
        {
            writer.WriteInt32(Server.worldData.mapCmds.Count);
            foreach (var kv in Server.worldData.mapCmds)
            {
                int mapId = kv.Key;

                //MultiplayerServer.instance.SendCommand(CommandType.CreateMapFactionData, ScheduledCommand.NoFaction, mapId, ByteWriter.GetBytes(factionId));

                List<byte[]> mapCmds = kv.Value;

                writer.WriteInt32(mapId);

                writer.WriteInt32(mapCmds.Count);
                foreach (var arr in mapCmds)
                    writer.WritePrefixedBytes(arr);
            }
        }

        if (streaming)
        {
            // No mapData in the initial payload. The currentMapIndex was stripped from savedGame
            // above (WorldData.GetSavedGameForStreaming), so the client loads cleanly into world
            // view; concrete maps arrive lazily via Server_MapResponse on PlayerCount transitions.
            writer.WriteInt32(0);
        }
        else
        {
            writer.WriteInt32(Server.worldData.mapData.Count);

            foreach (var kv in Server.worldData.mapData)
            {
                int mapId = kv.Key;
                byte[] mapData = kv.Value;

                writer.WriteInt32(mapId);
                writer.WritePrefixedBytes(mapData);
            }
        }

        writer.WriteInt32(Server.worldData.syncInfos.Count);
        foreach (var syncInfo in Server.worldData.syncInfos)
            writer.WritePrefixedBytes(syncInfo);

        byte[] packetData = writer.ToArray();
        connection.SendFragmented(Packets.Server_WorldData, packetData);

        ServerLog.Log("World response sent: " + packetData.Length);
    }
}
