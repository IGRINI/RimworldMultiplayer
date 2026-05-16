using System;
using System.Collections.Generic;
using System.Linq;
using Multiplayer.Common.Networking;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class ServerPlayingState(ConnectionBase conn) : MpConnectionState(conn)
    {
        // Once a player overflows their streaming buffer they are terminal: a Disconnect is queued
        // on Server.Enqueue and runs on the next action-queue drain. Until that drain, packets keep
        // arriving and any handler we run is a state-mutation path that defeats the disconnect
        // (auth checks alone don't help — host/arbiter overflow is possible). Override the
        // dispatch lookup so EVERY non-fragmented packet from a terminal player is consumed by a
        // no-op until the disconnect actually fires. The reader is seeked to the end so
        // HandleReceiveRaw doesn't log "Packet was not fully consumed".
        //
        // Fragment is intentionally false: an attacker could otherwise trigger HandleReceiveFragment
        // with arbitrary packet ids and allocate up to MaxFragmentPacketTotalSize for a
        // FragmentedPacket buffer in the disconnect window. With Fragment=false a fragmented packet
        // from a terminal player throws PacketReadException immediately, which escalates to the
        // existing ServerPacketRead disconnect path — same destination, no allocation.
        private static readonly PacketHandlerInfo TerminalNoOp =
            new((object target, ByteReader data) => data.Seek(data.Length), Fragment: false);

        public override PacketHandlerInfo? GetPacketHandler(Packets id)
        {
            if (connection.serverPlayer is { pendingBufferOverflowed: true })
                return TerminalNoOp;
            return base.GetPacketHandler(id);
        }

        [PacketHandler(Packets.Client_WorldReady)]
        public void HandleWorldReady(ByteReader data)
        {
            Player.UpdateStatus(PlayerStatus.Playing);
        }

        [PacketHandler(Packets.Client_RequestRejoin)]
        public void HandleRejoin(ByteReader data)
        {
            // Terminal: overflow Disconnect already queued. Honouring rejoin in this gap would
            // reset the latch and put the player back into a fresh loading flow, defeating the
            // disconnect. Drop the request — the queued Disconnect will close the connection on
            // the next action-queue drain; the client can then start a new connection cleanly.
            if (Player.pendingBufferOverflowed) return;

            // Rejoin throws away the client's whole world (it'll receive a fresh Server_WorldData
            // and Loader.ReloadGame from scratch), so any maps it had loaded before are gone. Clear
            // the streaming mirror or the server keeps thinking those maps are loaded — the next
            // PlayerCount on such a map would skip MapResponse and route map-scoped cmds to a
            // client that has no map data again. Counter and buffer get re-seeded by SendWorldData.
            //
            // mapTransferIds is intentionally NOT cleared. Generations must stay monotonic across
            // the connection lifetime so a delayed Client_MapLoaded(mapId, K) from before the
            // rejoin can never match the post-rejoin generation (which is strictly K+1 or
            // greater). Clearing here would let an old K=1 ack drain the pending buffer of a new
            // K=1 transfer — the bug the generation was supposed to prevent.
            Player.loadedMapIds.Clear();
            Player.inFlightMapIds.Clear();
            Player.pendingMapCmds.Clear();
            Player.pendingBufferOverflowed = false;
            Player.sentCmdsCount = 0;

            connection.ChangeState(ConnectionStateEnum.ServerLoading);
            Player.ResetTimeVotes();
        }

        [TypedPacketHandler]
        public void HandleDesynced(ClientDesyncedPacket packet) =>
            Server.playerManager.OnDesync(Player, packet.tick, packet.diffAt);

        [TypedPacketHandler]
        public void HandleTraces(ClientTracesPacket packet)
        {
            if (!RequireHost()) return;
            Server.GetPlayer(packet.playerId)?.SendPacket(ServerTracesPacket.Transfer(packet.rawTraces, packet.rawJittedMethods));
        }

        // Section 8: send the cached host worldData snapshot back to the requesting (desynced)
        // peer for post-mortem diff. No host round-trip — the server already holds the gzipped
        // save in memory from the last Client_WorldDataUpload. Empty array if nothing cached
        // yet (early-session desync); the client side handles the empty case gracefully.
        //
        // Gated on PlayerStatus.Desynced + a long per-player cooldown: the payload is the entire
        // world save (potentially many MB) and a healthy client has no business asking for it.
        // Without these gates a client could spam the request and force the server to fragment
        // the save out repeatedly.
        [TypedPacketHandler]
        public void HandleRequestHostSave(ClientRequestHostSavePacket _)
        {
            if (Player.status != PlayerStatus.Desynced) return;
            if (!Player.RateLimitAllow("hostSave", MultiplayerServer.NetTicksPerSecond * 30)) return;

            var saved = Server.worldData?.savedGame ?? System.Array.Empty<byte>();
            Player.SendPacket(new ServerHostSaveTransferPacket(saved));
        }

        [TypedPacketHandler]
        public void HandleClientCommand(ClientCommandPacket packet)
        {
            // Terminal: overflow Disconnect queued. Reject before any state mutation. Without this,
            // a PlayerCount packet in the gap would still set currentMapId/hasReportedCurrentMap,
            // add to inFlightMapIds, and trigger SendMapResponse — bypassing the per-recipient
            // guard in CommandHandler.Send (which only fires after these mutations).
            if (Player.pendingBufferOverflowed) return;

            int? mapToResync = null;

            if (packet.type == CommandType.PlayerCount)
            {
                ByteReader reader = new ByteReader(packet.data);
                var prevMapId = reader.ReadInt32();
                var newMapId = reader.ReadInt32();
                if (Player.currentMapId != prevMapId)
                    ServerLog.Error($"Inconsistent player {Player.Username} map. Last known map: {Player.currentMapId}, " +
                                    $"however received command with transition: {prevMapId} -> {newMapId}");
                Player.currentMapId = newMapId;
                Player.hasReportedCurrentMap = true;

                // Streaming: schedule a MapResponse iff this is a streamable mapId the player doesn't
                // already have loaded AND isn't already in flight. inFlightMapIds.Add returns false
                // if the entry was already present — that handles a duplicate PlayerCount(→same map)
                // arriving before the original ack: we keep the original in-flight request and the
                // ack-when-it-comes settles it once. A second MapResponse + subsequent ack would
                // reload the client unnecessarily.
                if (Server.CanUseStandaloneMapStreaming(newMapId)
                    && !Player.loadedMapIds.Contains(newMapId)
                    && Player.inFlightMapIds.Add(newMapId))
                {
                    mapToResync = newMapId;
                }
            }

            // todo check if map id is valid for the player

            Server.commands.Send(packet.type, Player.FactionId, packet.mapId, packet.data, Player);

            if (mapToResync is int currentMapId)
                Server.SendMapResponse(Player, currentMapId);
        }

        [TypedPacketHandler]
        public void HandleMapLoaded(ClientMapLoadedPacket packet)
        {
            int mapId = packet.mapId;
            if (mapId < 0) return; // sanity: sentinels never reach this path

            // Terminal: an overflow Disconnect is already queued for this player. Acks arriving in
            // the window before the action-queue drain must NOT drain the partial buffer or clear
            // the latch — that would re-open live routing in the gap and deliver a truncated cmd
            // stream. Drop quietly; the queued Disconnect will close the connection shortly.
            if (Player.pendingBufferOverflowed) return;

            // Generation gate: the ack must reference the *current* transfer for this (player,
            // mapId). A Rejoin clears mapTransferIds and a re-stream bumps it; an ack carrying an
            // old id belongs to a transfer that's already been superseded. Drop quietly — letting
            // it through would either drain the buffer for a transfer the client no longer has
            // loaded, or remove the still-in-flight current generation from inFlightMapIds before
            // its real ack lands.
            if (!Player.mapTransferIds.TryGetValue(mapId, out var currentTransferId)
                || currentTransferId != packet.transferId)
                return;

            // Only honour acks for maps the server actually requested. inFlightMapIds.Remove returns
            // false if mapId wasn't tracked — that covers stale duplicates AND unsolicited/malicious
            // acks. Without this gate the client could mark arbitrary maps as loaded and have
            // future map-scoped cmds routed to it without the data ever being sent.
            if (!Player.inFlightMapIds.Remove(mapId))
                return;

            Player.loadedMapIds.Add(mapId);

            // Drain only when the LAST in-flight transfer settles. Rapid switches that left several
            // requests in flight still produce a single ordered drain at the end of the chain — the
            // buffered stream covers all transitions and replays in arrival order.
            if (Player.inFlightMapIds.Count > 0) return;

            if (Player.pendingMapCmds.Count > 0)
            {
                // Each buffered packet was already finalized in CommandHandler.Send with a
                // baked-in per-player seq, and sentCmdsCount was incremented at buffer time.
                // Just push the bytes — incrementing again here would skew the seq baseline
                // and the next live cmd would arrive at the client with a stale seq.
                foreach (var raw in Player.pendingMapCmds)
                    Player.conn.Send(new SerializedPacket(Packets.Server_Command, raw), true);
                Player.pendingMapCmds.Clear();
            }
        }

        public const int MaxChatMsgLength = 128;

        private const int MaxMapsCount = 64;
        private const int MaxMapDataBytes = 16 * 1024 * 1024;
        private const int MaxSavedGameBytes = 16 * 1024 * 1024;
        private const int MaxSessionDataBytes = 4 * 1024 * 1024;

        [TypedPacketHandler]
        public void HandleChat(ClientChatPacket packet)
        {
            string msg = packet.msg;
            msg = msg.Trim();

            if (msg.Length == 0) return;

            // Length cap is enforced AFTER trimming so a short message padded with whitespace can't
            // bypass the limit. Drop (rather than truncate or disconnect): truncating would silently
            // mangle commands like /kick <user>, and disconnecting is too aggressive for chat abuse.
            // Log so an admin watching the server console can see who's tripping the cap.
            if (msg.Length > MaxChatMsgLength)
            {
                ServerLog.Log($"[Chat] Dropping {msg.Length}-char message from {connection.username} (cap is {MaxChatMsgLength})");
                return;
            }

            if (msg[0] == '/')
            {
                var cmd = msg[1..];
                Server.HandleChatCmd(Player, cmd);
            }
            else
            {
                Server.SendChat($"{connection.username}: {msg}");
            }
        }

        [PacketHandler(Packets.Client_WorldDataUpload, allowFragmented: true)]
        public void HandleWorldDataUpload(ByteReader data)
        {
            // On standalone, accept from any playing client; otherwise only host/arbiter
            if (!Server.IsStandaloneServer && !RequireArbiterOrHost())
                return;

            ServerLog.Detail($"Got world upload {data.Left}");

            int maps = data.ReadInt32();
            if (maps < 0 || maps > MaxMapsCount)
                throw new ReaderException($"Too many maps ({maps}>{MaxMapsCount})");

            // Parse into locals first so a rejection mid-stream doesn't leave worldData with empty
            // mapData while savedGame/sessionData still hold the previous snapshot.
            //
            // mapId shape: must be non-negative (negative ids are sentinels — Global, world view,
            // disconnect — and never name a real map) and unique within the upload (duplicates
            // would silently overwrite earlier entries, masking client/state corruption upstream).
            // Reject via ReaderException to match the count check above; the worldData snapshot
            // stays untouched because we only commit after the whole stream parses cleanly.
            var mapData = new Dictionary<int, byte[]>(maps);
            for (int i = 0; i < maps; i++)
            {
                int mapId = data.ReadInt32();
                if (mapId < 0)
                    throw new ReaderException($"Negative mapId in world upload: {mapId}");
                if (!mapData.ContainsKey(mapId))
                    mapData[mapId] = data.ReadPrefixedBytes(MaxMapDataBytes);
                else
                    throw new ReaderException($"Duplicate mapId in world upload: {mapId}");
            }

            var savedGame = data.ReadPrefixedBytes(MaxSavedGameBytes);
            var sessionData = data.ReadPrefixedBytes(MaxSessionDataBytes);

            Server.worldData.mapData = mapData;
            Server.worldData.savedGame = savedGame;
            Server.worldData.sessionData = sessionData;

            if (Server.worldData.CreatingJoinPoint)
                Server.worldData.EndJoinPointCreation();
        }

        [TypedPacketHandler]
        public void HandleStandaloneWorldSnapshot(ClientStandaloneWorldSnapshotPacket packet)
        {
            if (!Server.IsStandaloneServer)
                return;

            if (!Player.IsPlaying)
                return;

            var accepted = Server.worldData.TryAcceptStandaloneWorldSnapshot(Player, packet.tick,
                packet.worldData, packet.sessionData, packet.sha256Hash);

            if (accepted)
            {
                ServerLog.Detail(
                    $"Accepted standalone world snapshot tick={packet.tick} from {Player.Username}");
            }
            else
            {
                ServerLog.Detail(
                    $"Rejected standalone world snapshot tick={packet.tick} from {Player.Username}");
            }
        }

        [TypedPacketHandler]
        public void HandleStandaloneMapSnapshot(ClientStandaloneMapSnapshotPacket packet)
        {
            if (!Server.IsStandaloneServer)
                return;

            if (!Player.IsPlaying)
                return;

            var accepted = Server.worldData.TryAcceptStandaloneMapSnapshot(Player, packet.mapId, packet.tick,
                packet.mapData, packet.sha256Hash);

            if (accepted)
            {
                ServerLog.Detail(
                    $"Accepted standalone map snapshot map={packet.mapId} tick={packet.tick} from {Player.Username}");
            }
            else
            {
                ServerLog.Detail(
                    $"Rejected standalone map snapshot map={packet.mapId} tick={packet.tick} from {Player.Username}");
            }
        }

        [TypedPacketHandler]
        public void HandleCursor(ClientCursorPacket clientPacket)
        {
            if (Player.lastCursorTick == Server.NetTimer) return; // policy
            Player.lastCursorTick = Server.NetTimer;

            var serverPacket = new ServerCursorPacket(Player.id, clientPacket);
            Server.SendToIngame(serverPacket, reliable: false, excluding: Player);
        }

        // Rate-limit budgets are expressed in NetTicks (NetTicksPerSecond=30). Selected/ping/freeze
        // are best-effort UI updates — silently dropping over-budget packets is the correct policy:
        // disconnecting would punish UI lag, queueing would amplify it. Cursor already has its own
        // dedup via lastCursorTick and stays out of the generic limiter.
        private const int SelectedMinIntervalNetTicks = 3; // ~10 Hz
        private const int PingMinIntervalNetTicks = 6;     // ~5 Hz
        private const int FreezeMinIntervalNetTicks = 15;  // ~2 Hz

        [TypedPacketHandler]
        public void HandleSelected(ClientSelectedPacket packet)
        {
            if (!Player.RateLimitAllow("selected", SelectedMinIntervalNetTicks)) return;
            Server.SendToPlaying(new ServerSelectedPacket(Player.id, packet), excluding: Player);
        }

        [TypedPacketHandler]
        public void HandlePing(ClientPingLocPacket packet)
        {
            if (!Player.RateLimitAllow("ping", PingMinIntervalNetTicks)) return;
            Server.SendToPlaying(new ServerPingLocPacket(Player.id, packet));
        }

        [TypedPacketHandler]
        public void HandleClientKeepAlive(ClientKeepAlivePacket packet)
        {
            Player.ticksBehind = packet.ticksBehind;
            Player.ticksBehindReceivedAt = Server.gameTimer;
            Player.simulating = packet.simulating;
            Player.keepAliveAt = Server.NetTimer;

            if (Player.IsHost)
                Server.workTicks = packet.workTicks;

            var idMatched = Player.keepAliveId == packet.id;
            connection.OnKeepAliveArrived(idMatched);
            if (idMatched) Player.keepAliveId++;
        }

        [TypedPacketHandler]
        public void HandleDesyncCheck(ClientSyncInfoPacket packet)
        {
            if (!RequireArbiterOrHost()) return;

            // Keep at most 10 sync infos
            Server.worldData.syncInfos.Add(packet.rawSyncOpinion);
            if (Server.worldData.syncInfos.Count > 10)
                Server.worldData.syncInfos.RemoveAt(0);

            // The arbiter, when present, is the authoritative source - so don't forward
            // its opinion to itself, and forward to the host only when no arbiter is playing.
            var arbiter = Server.ArbiterPlaying;
            foreach (var p in Server.PlayingPlayers.Where(p => !p.IsArbiter && (arbiter || !p.IsHost)))
                p.conn.SendFragmented(new ServerSyncInfoPacket { rawSyncOpinion = packet.rawSyncOpinion }.Serialize());
        }

        [TypedPacketHandler]
        public void HandleFreeze(ClientFreezePacket packet)
        {
            if (!Player.RateLimitAllow("freeze", FreezeMinIntervalNetTicks)) return;

            Player.frozen = packet.freeze;

            if (!packet.freeze)
                Player.unfrozenAt = Server.NetTimer;
        }

        [TypedPacketHandler]
        public void HandleAutosaving(ClientAutosavingPacket packet)
        {
            var forceJoinPoint = packet.reason == JoinPointRequestReason.Save;

            ServerLog.Detail(
                $"Received Client_Autosaving from {Player.Username}, standalone={Server.IsStandaloneServer}, " +
                $"isHost={Player.IsHost}, reason={packet.reason}, force={forceJoinPoint}");

            // On standalone, any playing client can trigger a join point (always, regardless of settings)
            // On hosted, only the host can trigger and only if the Autosave flag is set
            if (Server.IsStandaloneServer ||
                (Player.IsHost && Server.settings.autoJoinPoint.HasFlag(AutoJoinPointFlags.Autosave)))
                Server.worldData.TryStartJoinPointCreation(forceJoinPoint, sourcePlayer: Player);
        }

        [TypedPacketHandler]
        public void HandleDebug(ClientDebugPacket _)
        {
            if (!RequireDevMode()) return;

            Server.worldData.mapCmds.Clear();
            Server.gameTimer = Server.startingTimer;

            Server.SendToPlaying(new ServerDebugPacket());
        }

        [TypedPacketHandler]
        public void HandleSetFaction(ClientSetFactionPacket packet)
        {
            int playerId = packet.playerId;
            int factionId = packet.factionId;

            if (!CanSetFactionOf(playerId)) return;

            var player = Server.GetPlayer(playerId);
            if (player == null) return;
            if (player.FactionId == factionId) return;

            player.FactionId = factionId;
            Server.SendToPlaying(new ServerSetFactionPacket(playerId, factionId));
        }

        // Players may change their own faction; only the host may change another player's faction.
        private bool CanSetFactionOf(int targetPlayerId) =>
            targetPlayerId == Player.id || Player.IsHost;

        [TypedPacketHandler]
        public void HandleFrameTime(ClientFrameTimePacket packet)
        {
            // The downstream clamp in MultiplayerServer.TickNet only bounds the AGGREGATE
            // serverTimePerTick, not the per-player value that feeds the maxFrameTime scan. A
            // single NaN or +Inf would propagate (NaN > x is always false, but +Inf trips the
            // upper clamp and then sticks; a huge finite value forces serverTimePerTick to its
            // max indefinitely). Reject NaN/Inf at the source and clamp the same range used
            // downstream so a misbehaving client can't poison time-control for everyone else.
            float ft = packet.frameTime;
            const float Min = MultiplayerServer.StandardTimePerTick;
            const float Max = MultiplayerServer.StandardTimePerTick * 4f;
            bool bad = float.IsNaN(ft) || float.IsInfinity(ft) || ft < Min || ft > Max;
            if (bad)
            {
                int now = Server.NetTimer;
                int cooldown = MultiplayerServer.NetTicksPerSecond * 2;
                if (now - Player.lastBadFrameTimeAt >= cooldown)
                {
                    ServerLog.Log($"[FrameTime] Bad value {ft} from {connection.username}; clamping to [{Min}..{Max}]");
                    Player.lastBadFrameTimeAt = now;
                }
                if (float.IsNaN(ft) || ft < Min) ft = Min;
                else if (ft > Max || float.IsPositiveInfinity(ft)) ft = Max;
            }
            Player.frameTime = ft;
        }
    }
}
