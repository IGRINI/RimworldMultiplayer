using System.Collections.Generic;
using System.Diagnostics;
using LudeonTK;
using Multiplayer.Client.Networking;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Steamworks;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client
{
    public class MultiplayerSession : IConnectionStatusListener
    {
        public string gameName;
        public int playerId;

        public int receivedCmds;
        public int remoteTickUntil;
        public int remoteSentCmds;

        public ConnectionBase client;
        public PacketLogWindow writerLog = new(true);
        public PacketLogWindow readerLog = new(false);
        public int myFactionId;
        public List<PlayerInfo> players = new();
        public GameDataSnapshot dataSnapshot;
        public LocationPings locationPings = new();
        public PlayerCursors playerCursors = new();
        public int autosaveCounter;
        public float? lastSaveAt;

        public bool replay;
        public int replayTimerStart = -1;
        public int replayTimerEnd = -1;
        public bool showTimeline;

        public bool desynced;

        // Per-mapId transferId from the most recent Server_MapResponse for that map. Echoed back in
        // Client_MapLoaded so the server can discard stale acks (e.g. from a transfer superseded by
        // Rejoin or re-stream). Updated in HandleMapResponse, consumed once when ReloadGame's
        // post-load callback fires the ack — either side may overwrite by simply re-streaming.
        public Dictionary<int, int> pendingMapTransferIds = new();

        public List<CSteamID> pendingSteam = new();
        public List<CSteamID> knownUsers = new();

        public const int MaxMessages = 200;
        public List<ChatMsg> messages = new();
        public bool hasUnread;
        public bool ghostModeCheckbox;

        public Process arbiter;
        public bool ArbiterPlaying => players.Any(p => p.type == PlayerType.Arbiter && p.status == PlayerStatus.Playing);

        public IConnector connector;

        public void Stop()
        {
            if (client != null)
            {
                client.Close(MpDisconnectReason.Internal);
                client.ChangeState(ConnectionStateEnum.Disconnected);
            }

            if (arbiter != null)
            {
                arbiter.TryKill();
                arbiter = null;
            }
        }

        public PlayerInfo GetPlayerInfo(int id) => players.FirstOrDefault(p => p.id == id);

        public void AddMsg(string msg, bool notify = true)
        {
            AddMsg(new ChatMsg_Text(msg), notify);
        }

        public void AddMsg(ChatMsg msg, bool notify = true)
        {
            var window = ChatWindow.Opened;

            if (window != null)
                window.OnChatReceived();
            else if (notify)
                NotifyChat();

            messages.Add(msg);

            if (messages.Count > MaxMessages)
                messages.RemoveAt(0);
        }

        public void NotifyChat()
        {
            hasUnread = true;
            SoundDefOf.PageChange.PlayOneShotOnCamera();
        }

        public void Reconnect(string username)
        {
            Multiplayer.username = username;
            ClientUtil.TryConnectWithWindow(connector);
        }

        public void Connected()
        {
        }

        public void Disconnected(SessionDisconnectInfo info)
        {
            MpUI.ClearWindowStack();

            Find.WindowStack.Add(new DisconnectedWindow(info)
            {
                returnToServerBrowser = Multiplayer.Client?.State != ConnectionStateEnum.ClientPlaying
            });
        }

        public void ReapplyPrefs()
        {
            Application.runInBackground = true;
        }

        public void ProcessTimeControl()
        {
            if (receivedCmds >= remoteSentCmds)
                TickPatch.tickUntil = remoteTickUntil;
        }

        // Fatal protocol-level desyncs (command sequence gap, late command in main queue, etc.)
        // come from the protocol layer rather than from a sync-opinion mismatch — they have no
        // SaveableDesyncInfo to compare. Centralise the "halt the simulation, surface a window,
        // tell the server" sequence here so every fatal site does the same thing.
        private bool protocolDesyncReported;
        public void TriggerProtocolDesync(string reason)
        {
            if (desynced) return;
            desynced = true;
            TickPatch.ClearSimulating();

            if (protocolDesyncReported) return;
            protocolDesyncReported = true;

            MpLog.Error($"Protocol desync: {reason}");
            try
            {
                Multiplayer.Client?.Send(new ClientDesyncedPacket(TickPatch.Timer, 0));
            }
            catch
            {
                // If the connection is already torn down, swallow — the window will still appear
                // and the user can rejoin manually.
            }

            OnMainThread.Enqueue(() =>
            {
                MpUI.ClearWindowStack();
                Find.WindowStack.Add(new DesyncedWindow(reason, null));
            });

            // Section 8 auto-rejoin opt-in. Window still appears so the user sees what happened
            // (and can cancel by closing the game / disabling the setting before the delay
            // elapses); rejoin proceeds automatically afterwards. Protocol desyncs have NO
            // SaveableDesyncInfo to write so a short delay is fine — only chat / log readability
            // matters here.
            if (Multiplayer.settings.autoRejoinOnDesync)
                OnMainThread.Schedule(static () =>
                {
                    if (Multiplayer.Client != null && Multiplayer.session != null && Multiplayer.session.desynced)
                        Rejoiner.DoRejoin();
                }, 1.5f);
        }

        [TweakValue("Multiplayer")] public static bool consistentCommandOrder = true;
        public void ScheduleCommand(ScheduledCommand cmd)
        {
            MpLog.Debug(cmd.ToString());
            dataSnapshot.MapCmds.GetOrAddNew(cmd.mapId).Add(cmd);

            if (Current.ProgramState != ProgramState.Playing) return;

            // Minimal code impact fix for #733. Having all the commands be added to a single queue gets rid of the
            // out-of-order execution problem.
            if (cmd.mapId == ScheduledCommand.Global || consistentCommandOrder)
                Multiplayer.AsyncWorldTime.Cmds.Enqueue(cmd);
            else
                cmd.GetMap()?.AsyncTime().Cmds.Enqueue(cmd);
        }

        public void Update()
        {
            locationPings.UpdatePing();
        }
    }
}
