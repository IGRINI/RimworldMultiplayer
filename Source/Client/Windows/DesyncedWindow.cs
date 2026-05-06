using JetBrains.Annotations;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public class DesyncedWindow : Window
    {
        const int NumButtons = 5;
        const float ButtonsWidth = 120 * NumButtons + 10 * (NumButtons - 1);

        public override Vector2 InitialSize => new(30 + 130 * NumButtons, 110);

        private string text;
        [CanBeNull] private readonly SaveableDesyncInfo desyncInfo;
        private float openedAt;
        private bool infoWritten;
        private bool rejoining;
        [CanBeNull] private SaveableDesyncInfo.HostInfo hostInfo;

        // Section 8 paired save: stash whatever the server replied with for Client_RequestHostSave
        // so it can be folded into the desync zip alongside Traces/JittedMethods. Either piece may
        // arrive in any order; both feed into the single HostInfo passed to SaveableDesyncInfo.Save.
        private byte[] hostSavedGame;

        public DesyncedWindow(string text, [CanBeNull] SaveableDesyncInfo desyncInfo)
        {
            this.text = text;
            this.desyncInfo = desyncInfo;

            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = false;
            absorbInputAroundWindow = true;
            openedAt = Time.realtimeSinceStartup;

            layer = WindowLayer.Super;

#if DEBUG
            doCloseX = true;
#endif

            // Section 8: kick off the host-save fetch as soon as the window is constructed so the
            // bytes are likely available by the time WindowUpdate decides to flush the report.
            // Only meaningful when there's an actual desyncInfo to write into; protocol desyncs
            // skip the report path entirely, so don't waste a packet round-trip.
            if (desyncInfo != null && Multiplayer.Client != null)
            {
                try { Multiplayer.Client.Send(new ClientRequestHostSavePacket()); }
                catch { /* connection torn down — report still writes with no host save. */ }
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;

            Text.Anchor = TextAnchor.UpperCenter;
            var label = "MpDesynced".Translate();
            if (MpVersion.IsDebug || Prefs.DevMode) label += "\n" + text;
            else label += "\n" + "MpDesyncedSubtitle".Translate();
            Widgets.Label(new Rect(0, 0, inRect.width, 40), label);
            Text.Anchor = TextAnchor.UpperLeft;

            var buttonsRect = new Rect((inRect.width - ButtonsWidth) / 2, 40, ButtonsWidth, 35);

            GUI.BeginGroup(buttonsRect);

            float x = 0;
            if (Widgets.ButtonText(new Rect(x, 0, 120, 35), "MpTryResync".Translate()) && !rejoining)
            {
                rejoining = true;
                Log.Message("Multiplayer: requesting rejoin");
                Rejoiner.DoRejoin();
            }

            x += 120 + 10;

            if (Widgets.ButtonText(new Rect(x, 0, 120, 35), "Save".Translate()))
                Find.WindowStack.Add(new SaveGameWindow(Multiplayer.session.gameName) { layer = WindowLayer.Super });
            x += 120 + 10;

            if (Widgets.ButtonText(new Rect(x, 0, 120, 35), "MpChatButton".Translate()))
                Find.WindowStack.Add(new ChatWindow
                {
                    closeOnClickedOutside = true,
                    absorbInputAroundWindow = true,
                    saveSize = false,
                    layer = WindowLayer.Super
                });
            x += 120 + 10;

            var openDesyncsFolder = MpUI.ButtonTextWithTip(
                new Rect(x, 0, 120, 35),
                "MpOpenDesyncFolder".Translate(),
                infoWritten ? null : "MpDesyncedWaiting".Translate() + MpUI.FixedEllipsis(), !infoWritten, 9793021
            );
            if (openDesyncsFolder)
                ShellOpenDirectory.Execute(Multiplayer.DesyncsDir);
            x += 120 + 10;

            if (Widgets.ButtonText(new Rect(x, 0, 120, 35), "Quit".Translate()))
                MainMenuPatch.AskQuitToMainMenu();

            GUI.EndGroup();
        }

        public void HandleHostDesyncInfo(SaveableDesyncInfo.HostInfo hostInfo)
        {
            // Section 8: merge the freshly-arrived traces/jittedMethods with any host save bytes
            // that have already arrived (or will arrive later). The two payloads are independent
            // — the host save flows from a different packet pair (Client_RequestHostSave →
            // Server_HostSaveTransfer) than the traces (ServerTracesPacket Request/Transfer).
            this.hostInfo = hostInfo with { HostSavedGame = hostSavedGame ?? hostInfo.HostSavedGame };
        }

        // Called by ClientPlayingState.HandleHostSaveTransfer. Folds the host save bytes into the
        // existing hostInfo if present; otherwise stashes them so the next HandleHostDesyncInfo
        // call merges them in.
        public void HandleHostSavedGame(byte[] savedGame)
        {
            hostSavedGame = savedGame;
            if (hostInfo != null)
                hostInfo = hostInfo with { HostSavedGame = savedGame };
        }

        public override void WindowUpdate()
        {
            const float maxWait = 5f;

            // Protocol-level desyncs (e.g. command-stream gap) trigger this window without a
            // SaveableDesyncInfo — there's nothing to compare or write. Skip the report path.
            if (desyncInfo == null) return;

            var shouldWrite = hostInfo != null || Time.realtimeSinceStartup - openedAt > maxWait;
            if (!infoWritten && shouldWrite && desyncInfo.ReadyToSave)
            {
                // Even if hostInfo is still null after maxWait, fold in any host save we did
                // receive so the report at least contains that.
                var infoForSave = hostInfo;
                if (infoForSave == null && hostSavedGame != null)
                    infoForSave = new SaveableDesyncInfo.HostInfo(null, null, hostSavedGame);
                desyncInfo.Save(infoForSave);
                infoWritten = true;
            }
        }
    }

}
