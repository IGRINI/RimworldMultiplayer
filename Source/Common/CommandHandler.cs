using System;
using System.Linq;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class CommandHandler
    {
        private MultiplayerServer server;

        public int SentCmds { get; private set; }

        public CommandHandler(MultiplayerServer server)
        {
            this.server = server;
        }

        public void Send(CommandType cmdType, int factionId, int mapId, byte[] data, ServerPlayer? sourcePlayer = null, ServerPlayer? fauxSource = null)
        {
            // policy
            if (sourcePlayer != null)
            {
                bool debugCmd =
                    cmdType == CommandType.DebugTools ||
                    cmdType == CommandType.Sync && server.InitData!.DebugOnlySyncCmds.Contains(BitConverter.ToInt32(data, 0));
                if (debugCmd && !CanUseDevMode(sourcePlayer))
                    return;

                bool hostOnly = cmdType == CommandType.Sync && server.InitData!.HostOnlySyncCmds.Contains(BitConverter.ToInt32(data, 0));
                if (hostOnly && !sourcePlayer.IsHost)
                    return;

                if (cmdType is CommandType.MapTimeSpeed or CommandType.GlobalTimeSpeed &&
                    server.settings.timeControl == TimeControl.HostOnly &&
                    !sourcePlayer.IsHost &&
                    server.PlayingPlayers.Any(p => p.IsHost))
                    return;

                // Terminal: this player overflowed their streaming pending buffer and a Disconnect
                // is queued but hasn't run yet. Reject their cmds outright before mutating worldData
                // or incrementing SentCmds — otherwise they'd keep polluting authoritative state in
                // the window between enqueue and the next action-queue drain.
                if (sourcePlayer.pendingBufferOverflowed)
                    return;
            }

            var cmd = new ScheduledCommand(
                cmdType,
                server.gameTimer,
                factionId,
                mapId,
                sourcePlayer?.id ?? fauxSource?.id ?? ScheduledCommand.NoPlayer,
                data);
            byte[] toSave = ScheduledCommand.Serialize(cmd);

            // todo cull target players if not global
            server.worldData.mapCmds.GetOrAddNew(mapId).Add(toSave);
            server.worldData.tmpMapCmds?.GetOrAddNew(mapId).Add(toSave);

            // Standalone streaming routes per-player. A cmd is "relevant" to a player if:
            //   - it's global (mapId < 0), OR
            //   - the player has the map loaded, OR
            //   - the map is currently in-flight to this player (MapResponse sent, ack pending)
            // Irrelevant cmds are dropped from this player's stream entirely; if they ever switch
            // to that map, the MapResponse snapshot will replay them from worldData.mapCmds[mapId].
            //
            // For *relevant* cmds: if there's any in-flight transfer to this player, we buffer to
            // preserve server-order. The client's Cmds is a plain Queue<>; TickPatch.RunCmds peeks
            // by ticks==curTimer and stalls on out-of-order entries. While loading the client is
            // paused (replayTimeSpeed=Paused), so buffering doesn't degrade responsiveness.
            if (server.IsStandaloneServer)
            {
                var serialized = ServerCommandPacket.From(cmd).Serialize();
                foreach (var player in server.PlayingPlayers)
                {
                    bool relevant = mapId < 0
                                    || player.loadedMapIds.Contains(mapId)
                                    || player.inFlightMapIds.Contains(mapId);
                    if (!relevant) continue;

                    if (player.inFlightMapIds.Count > 0)
                    {
                        if (player.pendingBufferOverflowed)
                        {
                            // Disconnect already enqueued for this player; the deferred Disconnect
                            // hasn't run yet (we're inside a server tick, before the action queue
                            // drain). Drop quietly to avoid scheduling the same disconnect twice
                            // and to avoid pretending the client is still in sync.
                            continue;
                        }
                        if (player.pendingMapCmds.Count >= ServerPlayer.MaxPendingMapCmds)
                        {
                            // Terminal policy: silently dropping cmds would put the client into a
                            // guaranteed desync (server's worldData and SentCmds keep advancing,
                            // but the client never sees these cmds). The honest outcome is to
                            // disconnect so the client gets an explicit signal and can rejoin.
                            // Schedule on the action queue so we don't mutate PlayingPlayers from
                            // inside this foreach.
                            ServerLog.Error($"Streaming pending buffer cap reached for {player.Username} ({ServerPlayer.MaxPendingMapCmds} cmds); disconnecting.");
                            player.pendingBufferOverflowed = true;
                            var captured = player;
                            server.Enqueue(() => captured.Disconnect("MpStreamingPendingBufferOverflow"));
                            continue;
                        }
                        player.pendingMapCmds.Add(serialized.data);
                    }
                    else
                    {
                        player.conn.Send(serialized, true);
                        player.sentCmdsCount++;
                    }
                }
            }
            else
            {
                // Embedded host (legacy): single broadcast, global sentCmdsSnapshot drives TimeControl.
                server.SendToPlaying(ServerCommandPacket.From(cmd));
            }

            SentCmds++;
        }

        public void PauseAll()
        {
            if (server.settings.timeControl == TimeControl.LowestWins)
                Send(
                    CommandType.TimeSpeedVote,
                    ScheduledCommand.NoFaction,
                    ScheduledCommand.Global,
                    ByteWriter.GetBytes(TimeVote.ResetGlobal, -1)
                );
            else
                Send(
                    CommandType.PauseAll,
                    ScheduledCommand.NoFaction,
                    ScheduledCommand.Global,
                    Array.Empty<byte>()
                );
        }

        public bool CanUseDevMode(ServerPlayer player) =>
            server.settings.debugMode && server.settings.devModeScope switch
            {
                DevModeScope.Everyone => true,
                DevModeScope.HostOnly => player.IsHost
            };
    }
}
