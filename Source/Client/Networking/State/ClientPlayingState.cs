using System;
using System.Collections.Generic;
using System.Linq;
using Ionic.Zlib;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Patches;
using Multiplayer.Client.Saving;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    [PacketHandlerClass(inheritHandlers: true)]
    public class ClientPlayingState(ConnectionBase connection) : ClientBaseState(connection)
    {
        public override void StartState()
        {
            VTRSync.ForceReportCurrentView();
        }

        [TypedPacketHandler]
        public void HandleCommand(ServerCommandPacket packet)
        {
            // Per-recipient seq enforcement. The server stamps a monotonic seq on every cmd packet
            // emitted to this player; any gap, duplicate, or reorder is a protocol-level desync —
            // simulating onward would silently diverge from peers. Halt and surface a window so
            // the user gets an explicit signal and a rejoin path.
            if (packet.seq != Multiplayer.session.receivedCmds)
            {
                Multiplayer.session.TriggerProtocolDesync(
                    $"Command sequence gap: expected seq {Multiplayer.session.receivedCmds}, received {packet.seq} (cmdType={packet.type}, ticks={packet.ticks}, mapId={packet.mapId})");
                return;
            }

            Session.ScheduleCommand(packet.ToCommand());
            Multiplayer.session.receivedCmds++;
            Multiplayer.session.ProcessTimeControl();
        }

        [TypedPacketHandler]
        public void HandlePlayerList(ServerPlayerListPacket packet)
        {
            if (packet.action == PlayerListAction.Add)
            {
                foreach (var info in packet.players)
                {
                    if (!Multiplayer.session.players.Any(p => p.id == info.id || p.username == info.username))
                    {
                        ServerLog.Log($"PlayerList: Adding player {info.id}:{info.username}");
                        Multiplayer.session.players.Add(PlayerInfo.FromNet(info));
                    }
                    else
                    {
                        ServerLog.Error($"PlayerList: Adding player {info.id}:{info.username} - player already exists");
                    }
                }
            }
            else if (packet.action == PlayerListAction.Remove)
            {
                ServerLog.Log($"PlayerList: Removing player with id {packet.playerId}");
                var matches = Multiplayer.session.players.RemoveAll(p => p.id == packet.playerId);
                if (matches > 1)
                {
                    ServerLog.Error($"PlayerList: Removing player with id {packet.playerId} -- occurred {matches} times. This should not happen");
                }
            }
            else if (packet.action == PlayerListAction.List)
            {
                ServerLog.Log($"PlayerList: Received player list with {packet.players.Length} entries");

                Multiplayer.session.players.Clear();
                foreach (var info in packet.players)
                {
                    ServerLog.Log($"PlayerList: Adding player from list {info.id}:{info.username}");
                    Multiplayer.session.players.Add(PlayerInfo.FromNet(info));
                }
            }
            else if (packet.action == PlayerListAction.Latencies)
            {
                foreach (var latency in packet.latencies)
                {
                    var player = Multiplayer.session.GetPlayerInfo(latency.playerId);
                    if (player == null)
                    {
                        ServerLog.Log($"PlayerList: Received latency info for unknown player with id {latency.playerId}");
                        continue;
                    }
                    player.latency = latency.latency;
                    player.ticksBehind = latency.ticksBehind;
                    player.simulating = latency.simulating;
                    player.frameTime = latency.frameTime;
                }
            }
            else if (packet.action == PlayerListAction.Status)
            {
                var player = Multiplayer.session.GetPlayerInfo(packet.playerId);
                if (player == null)
                {
                    ServerLog.Log($"PlayerList: Received player status ({packet.status}) for unknown player with id {packet.playerId}");
                }
                else
                {
                    player.status = packet.status;
                }
            }
        }

        [TypedPacketHandler]
        public void HandleChat(ServerChatPacket packet) => Multiplayer.session.AddMsg(packet.msg);

        [TypedPacketHandler]
        public void HandleCursor(ServerCursorPacket packet)
        {
            var player = Multiplayer.session.GetPlayerInfo(packet.playerId);
            if (player == null) return;

            var data = packet.data;
            if (data.seq < player.cursorSeq && player.cursorSeq - data.seq < 128) return;

            player.map = data.map;
            if (data.map == byte.MaxValue) return;

            player.cursorSeq = data.seq;
            player.lastCursor = player.cursor;
            player.lastDelta = Multiplayer.clock.ElapsedMillisDouble() - player.updatedAt;
            player.cursor = new Vector3(data.x, 0, data.z);
            player.updatedAt = Multiplayer.clock.ElapsedMillisDouble();
            player.cursorIcon = data.icon;

            player.dragStart = data.HasDrag ? new Vector3(data.dragX, 0, data.dragZ) : PlayerInfo.Invalid;
        }

        [TypedPacketHandler]
        public void HandleSelected(ServerSelectedPacket packet)
        {
            var player = Multiplayer.session.GetPlayerInfo(packet.playerId);
            if (player == null) return;

            var data = packet.data;
            if (data.reset) player.selectedThings.Clear();

            foreach (var id in data.newlySelectedIds)
                player.selectedThings[id] = Time.realtimeSinceStartup;

            foreach (var id in data.unselectedIds)
                player.selectedThings.Remove(id);
        }

        [TypedPacketHandler]
        public void HandlePing(ServerPingLocPacket packet) => Session.locationPings.ReceivePing(packet);

        [PacketHandler(Packets.Server_MapResponse, allowFragmented: true)]
        public void HandleMapResponse(ByteReader data)
        {
            int mapId = data.ReadInt32();
            // transferId is per-(player, mapId) generation; bumped server-side on every
            // SendMapResponse for this mapId. Echo it back in ClientMapLoadedPacket so the server
            // can discard stale acks from prior transfers (e.g. superseded by Rejoin or re-stream).
            int transferId = data.ReadInt32();
            // snapshotCommandSeq documents which player.sentCmdsCount baseline the snapshot's
            // mapCmds were taken under. Currently not consumed by the client (snapshot cmds bypass
            // HandleCommand → no seq enforcement) but kept on the wire for future verification.
            int snapshotCommandSeq = data.ReadInt32();
            _ = snapshotCommandSeq;

            // Drop late MapResponses whose transferId is older than (or equal to) the latest one
            // we've already accepted for this map. Without this, a delayed transfer-1 response
            // arriving after we've started processing transfer-2 would overwrite dataSnapshot and
            // queue a second Loader.ReloadGame; the ack would be discarded server-side (good) but
            // the client would have already reloaded stale data (bad). The fix is to refuse the
            // stale response BEFORE mutating any session state.
            if (Multiplayer.session.pendingMapTransferIds.TryGetValue(mapId, out var latestTransferId)
                && transferId <= latestTransferId)
            {
                MpLog.Log($"Ignoring stale MapResponse(mapId={mapId}, transferId={transferId}); already at {latestTransferId}");
                int skipMapCmdsLen = data.ReadInt32();
                for (int j = 0; j < skipMapCmdsLen; j++) data.ReadPrefixedBytes();
                data.ReadPrefixedBytes();
                return;
            }

            int mapCmdsLen = data.ReadInt32();
            List<ScheduledCommand> mapCmds = new List<ScheduledCommand>(mapCmdsLen);
            for (int j = 0; j < mapCmdsLen; j++)
                mapCmds.Add(ScheduledCommand.Deserialize(new ByteReader(data.ReadPrefixedBytes())));

            Session.dataSnapshot.MapCmds[mapId] = mapCmds;

            byte[] mapData = GZipStream.UncompressBuffer(data.ReadPrefixedBytes());
            Session.dataSnapshot.MapData[mapId] = mapData;
            Multiplayer.session.pendingMapTransferIds[mapId] = transferId;

            // Capture the asyncTime flag now (main thread reads happen later inside the lambda).
            bool forceAsyncTime = Multiplayer.game?.gameComp.asyncTime ?? false;
            // Capture the transferId for this load. If a newer MapResponse for the same mapId
            // arrives before our queued ReloadGame runs, pendingMapTransferIds[mapId] will have
            // moved on; the callback below detects that and skips both the ack and any side
            // effects — the newer load will issue its own ack.
            int capturedTransferId = transferId;

            OnMainThread.Enqueue(() =>
            {
                // Pre-reload superseded check. By the time this lambda runs, a newer MapResponse
                // for the same mapId may have arrived and overwritten dataSnapshot[mapId]. We
                // do NOT want Loader.ReloadGame to fire for the older transferId because:
                //   1. The reload itself is the expensive, side-effecting step (it calls into
                //      ClearAllMapsAndWorld, scribes, runs PostLoad — flipping asyncTime, etc).
                //   2. The newer response will queue its own reload; running both in sequence
                //      double-loads and risks intermediate UI flicker and tick re-entry.
                // The callback below is a second line of defense for the rarer case where a
                // newer response arrives DURING the reload itself (after this gate but before
                // the post-load callback runs).
                if (Multiplayer.session.pendingMapTransferIds.TryGetValue(mapId, out var preReloadCurrent)
                    && preReloadCurrent != capturedTransferId)
                {
                    MpLog.Log($"MapResponse(mapId={mapId}, transferId={capturedTransferId}) superseded by {preReloadCurrent} before reload; skipping load");
                    return;
                }

                var mapsToLoad = Find.Maps.Select(m => m.uniqueID).Append(mapId).Distinct().ToList();
                // Use the customPostLoadAction overload: it runs after Loader.PostLoad on the main
                // thread, which is when the new map is in Find.Maps and dispatchable by
                // TickPatch.TickableById. Sending Client_MapLoaded earlier would race the load and
                // the server might unbuffer cmds for a map the client can't yet route to.
                Loader.ReloadGame(mapsToLoad, false, () =>
                {
                    if (forceAsyncTime) Multiplayer.game.gameComp.asyncTime = true;
                    if (Multiplayer.Client == null) return;

                    // Second defense layer: a newer MapResponse arrived DURING the reload (after
                    // the pre-reload check above). The newer load will execute next and emit its
                    // own ack; emitting one here would race the server's buffered-cmd drain.
                    if (Multiplayer.session.pendingMapTransferIds.TryGetValue(mapId, out var current)
                        && current != capturedTransferId)
                    {
                        MpLog.Log($"MapResponse(mapId={mapId}, transferId={capturedTransferId}) superseded by {current} during reload; skipping ack");
                        return;
                    }

                    Multiplayer.Client.Send(new ClientMapLoadedPacket(mapId, capturedTransferId));
                });
            });
        }

        [TypedPacketHandler]
        public void HandleNotification(ServerNotificationPacket packet)
        {
            var namedArgs = Array.ConvertAll(packet.args, s => (NamedArgument)s);
            var msg = packet.key.Translate(namedArgs);
            Messages.Message(msg, MessageTypeDefOf.SilentInput, false);
            ServerLog.Log($"Notification: {msg} ({packet.key}, {packet.args.Join(", ")})");
        }

        [TypedPacketHandler]
        public void HandleDesyncCheck(ServerSyncInfoPacket packet) =>
            Multiplayer.game?.sync.AddClientOpinionAndCheckDesync(ClientSyncOpinion.FromNet(packet.SyncOpinion));

        [TypedPacketHandler]
        public void HandleFreeze(ServerFreezePacket packet)
        {
            TickPatch.serverFrozen = packet.frozen;
            TickPatch.frozenAt = packet.gameTimer;
        }

        // Section 8: paired desync save. Server replies with the cached worldData snapshot when
        // the desynced client requested it (DesyncedWindow ctor sends Client_RequestHostSave).
        // Bytes go straight to the open window for inclusion in the desync zip; if the window has
        // already closed (or never opened, e.g. a protocol desync that skipped the report path),
        // we discard silently — there's nowhere to write.
        [TypedPacketHandler]
        public void HandleHostSaveTransfer(ServerHostSaveTransferPacket packet)
        {
            Find.WindowStack.WindowOfType<DesyncedWindow>()?.HandleHostSavedGame(packet.rawSavedGame);
        }

        [TypedPacketHandler]
        public void HandleTraces(ServerTracesPacket packet)
        {
            if (packet.mode == ServerTracesPacket.Mode.Request)
            {
                var info = Multiplayer.game.sync.knownClientOpinions.FirstOrDefault(b => b.startTick == packet.tick);
                var response = info?.GetFormattedStackTracesForRange(packet.diffAt) ?? "Traces not available";
                MpLog.Log(
                    $"Desync host trace request received: target={packet.playerId}, tick={packet.tick}, diffAt={packet.diffAt}, found={info != null}");

                connection.SendFragmented(new ClientTracesPacket
                {
                    playerId = packet.playerId,
                    rawTraces = GZipStream.CompressString(response),
                    rawJittedMethods = GZipStream.CompressString(JittedMethods.GetJittedMethodsString())
                }.Serialize());
            }
            else if (packet.mode == ServerTracesPacket.Mode.Transfer)
            {
                var traces = GZipStream.UncompressString(packet.rawTraces);
                var jittedMethods = GZipStream.UncompressString(packet.rawJittedMethods);
                MpLog.Log(
                    $"Desync host traces received: traceBytes={packet.rawTraces?.Length ?? 0}, jittedBytes={packet.rawJittedMethods?.Length ?? 0}");
                var hostInfo = new SaveableDesyncInfo.HostInfo(traces, jittedMethods);
                Find.WindowStack.WindowOfType<DesyncedWindow>()?.HandleHostDesyncInfo(hostInfo);
            }
        }

        [TypedPacketHandler]
        public void HandleDebug(ServerDebugPacket _) => Rejoiner.DoRejoin();

        [TypedPacketHandler]
        public void HandleSetFaction(ServerSetFactionPacket packet)
        {
            var playerId = packet.playerId;
            var factionId = packet.factionId;
            Session.GetPlayerInfo(playerId).factionId = factionId;

            if (Session.playerId == playerId)
            {
                Multiplayer.game.ChangeRealPlayerFaction(factionId);
                Session.myFactionId = factionId;
            }
        }
    }

}
