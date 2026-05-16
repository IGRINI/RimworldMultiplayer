using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class ServerPlayer : IChatSource
    {
        public int id;
        public ConnectionBase conn;
        public PlayerType type = PlayerType.Normal;
        public PlayerStatus status = PlayerStatus.Simulating;
        public ColorRGB color;
        public bool hasJoined;
        public bool simulating;
        public float frameTime;

        public int ticksBehind;
        public int ticksBehindReceivedAt;
        public int ExtrapolatedTicksBehind => ticksBehind + (Server.gameTimer - ticksBehindReceivedAt);

        public ulong steamId;
        public string steamPersonaName = "";

        public int lastCursorTick = -1;

        // Cooldown for HandleFrameTime warning logs. Stores the NetTimer at which the last bad-value
        // log fired so a flood of NaN/Inf/out-of-range frame times from one client doesn't spam
        // ServerLog. Compared against MultiplayerServer.NetTicksPerSecond * 2 in the handler.
        public int lastBadFrameTimeAt = int.MinValue;

        public int keepAliveId;
        public int keepAliveAt;

        public bool frozen;
        public int unfrozenAt;

        // Track which map the player is currently on
        public int currentMapId = -1;
        public bool hasReportedCurrentMap;

        // --- Map streaming state (only used when MultiplayerServer.CanUseStandaloneMapStreaming is true). ---
        // Maps the client has acknowledged loading via Client_MapLoaded. Cmds for these maps are sent
        // immediately when no transfer is in flight.
        public HashSet<int> loadedMapIds = new();
        // Maps for which the server has sent a Server_MapResponse and is still awaiting the matching
        // Client_MapLoaded ack. Multi-element to handle rapid PlayerCount switches: a second transition
        // can start before the first ack arrives. A Client_MapLoaded(mapId) is only honoured if mapId is
        // currently in this set — that gates the client from spuriously declaring arbitrary maps loaded.
        // While this set is non-empty, ALL relevant cmds (globals, cmds for in-flight maps, cmds for
        // already-loaded maps) are buffered into pendingMapCmds to preserve the strict server-order the
        // client's Cmds queue depends on. Cmds for unrelated unloaded maps are still skipped (delivered
        // later via MapResponse snapshot).
        public HashSet<int> inFlightMapIds = new();
        // Buffered Server_Command payloads (already serialized) waiting on the last in-flight map ack.
        // Drained in arrival order once inFlightMapIds becomes empty.
        public List<byte[]> pendingMapCmds = new();
        // Hard cap on pendingMapCmds entries. A client that requests a map and never sends
        // Client_MapLoaded would otherwise let the buffer grow until OOM. On reaching the cap,
        // CommandHandler.Send schedules a disconnect for this player (terminal policy: silently
        // dropping cmds would only produce a guaranteed desync). The latch prevents enqueueing
        // the same disconnect twice while the action queue hasn't drained yet.
        public const int MaxPendingMapCmds = 8192;
        public bool pendingBufferOverflowed;
        // Per-player count of Server_Command packets dispatched. Shipped to the client in ServerTimeControl
        // so MultiplayerSession.ProcessTimeControl unfreezes the simulation only after the client has caught
        // up to *its own* expected count (otherwise streaming-filtered cmds would freeze the client forever).
        public int sentCmdsCount;

        // Per-mapId generation for outstanding map transfers. Bumped each time SendMapResponse is
        // emitted for a (player, mapId). The matching Client_MapLoaded ack carries this id, and
        // HandleMapLoaded ignores acks whose id doesn't match the current generation — that handles
        // stale acks from prior transfers superseded by Rejoin or by a re-stream.
        public Dictionary<int, int> mapTransferIds = new();

        public string Username => conn.username;
        public int Latency => conn.Latency;
        public int FactionId { get; set; }
        public bool HasJoined => conn.State is ConnectionStateEnum.ServerLoading or ConnectionStateEnum.ServerPlaying;
        public bool IsPlaying => conn.State == ConnectionStateEnum.ServerPlaying;
        public bool IsHost => Server.hostUsername == Username;
        public bool IsArbiter => type == PlayerType.Arbiter;

        public MultiplayerServer Server => MultiplayerServer.instance!;

        public ServerPlayer(int id, ConnectionBase connection)
        {
            this.id = id;
            conn = connection;
        }

        public void HandleReceive(ByteReader data, bool reliable)
        {
            try
            {
                conn.HandleReceiveRaw(data, reliable);
            }
            catch (Exception e)
            {
                ServerLog.Error($"Error handling packet by {conn}: {e}");
                Disconnect(MpDisconnectReason.ServerPacketRead);
            }
        }

        public void Disconnect(string reasonKey)
        {
            Disconnect(MpDisconnectReason.GenericKeyed, ByteWriter.GetBytes(reasonKey));
        }

        public void Disconnect(MpDisconnectReason reason, byte[]? data = null)
        {
            conn.Close(reason, data);
            Server.playerManager.SetDisconnected(conn, reason);
        }

        public void SendPacket<T>(T packet, bool reliable = true) where T : struct, IPacket =>
            conn.Send(packet, reliable);

        public void SendKeepAlivePacket() =>
            SendPacket(new ServerKeepAlivePacket(keepAliveId), false);

        public void SendPlayerList() =>
            SendPacket(ServerPlayerListPacket.List(Server.JoinedPlayers.Select(p => p.PlayerInfoPacket())));

        public ServerPlayerListPacket.PlayerInfo PlayerInfoPacket() => new()
        {
            id = id,
            username = Username ?? "",
            latency = Latency,
            type = type,

            status = status,

            steamId = steamId,
            steamPersonaName = steamPersonaName,

            ticksBehind = ticksBehind,
            simulating = simulating,

            r = color.r,
            g = color.g,
            b = color.b,

            factionId = FactionId,
        };

        public ServerPlayerListPacket.PlayerLatency LatencyPacket() => new()
        {
            playerId = id, latency = Latency, ticksBehind = ticksBehind, simulating = simulating,frameTime = frameTime
        };

        public void UpdateStatus(PlayerStatus newStatus)
        {
            if (status == newStatus) return;
            status = newStatus;
            Server.SendToPlaying(ServerPlayerListPacket.Status(id, newStatus));
        }

        public void ResetTimeVotes()
        {
            Server.commands.Send(
                CommandType.TimeSpeedVote,
                ScheduledCommand.NoFaction,
                ScheduledCommand.Global,
                ByteWriter.GetBytes(TimeVote.PlayerResetGlobal, -1),
                fauxSource: this
            );
        }

        public void SendMsg(string msg) => SendPacket(ServerChatPacket.Create(msg));
    }

    public enum PlayerStatus : byte
    {
        Simulating,
        Playing,
        Desynced
    }

    public enum PlayerType : byte
    {
        Normal,
        Steam,
        Arbiter
    }
}
