using System;
using System.Linq;
using Ionic.Zlib;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client.Networking
{
    public class LocalConnection : ConnectionBase
    {
        public override int Latency { get => 0; set { } }
        public LocalConnection remoteConn;
        private bool isClient;
        private Action<Action> enqueue;

        private LocalConnection(string username, bool isClient, Action<Action> enqueue)
        {
            this.username = username;
            this.isClient = isClient;
            this.enqueue = enqueue;
        }

        public static LocalConnection Client(string username) => new(username, true, Multiplayer.LocalServer.Enqueue);
        public static LocalConnection Server(string username) => new(username, false, OnMainThread.Enqueue);

        public static (LocalConnection client, LocalConnection server) Paired(string username)
        {
            var localClient = Client(username);
            var localServer = Server(username);

            localClient.remoteConn = localServer;
            localServer.remoteConn = localClient;
            return (localClient, localServer);
        }

        public override bool TryEnqueueLocalHostDesyncTraces(int tick, int diffAt, int targetPlayerId)
        {
            if (isClient)
                return false;

            OnMainThread.Enqueue(() => SendLocalHostDesyncTraces(tick, diffAt, targetPlayerId));
            return true;
        }

        private static void SendLocalHostDesyncTraces(int tick, int diffAt, int targetPlayerId)
        {
            try
            {
                var sync = Multiplayer.game?.sync;
                var info = sync?.knownClientOpinions.FirstOrDefault(op => op.startTick == tick);
                var traces = info?.GetFormattedStackTracesForRange(diffAt) ?? "Traces not available";
                var jittedMethods = JittedMethods.GetJittedMethodsString();
                var packet = new ClientTracesPacket
                {
                    playerId = targetPlayerId,
                    rawTraces = GZipStream.CompressString(traces),
                    rawJittedMethods = GZipStream.CompressString(jittedMethods)
                };

                MpLog.Log(
                    $"Desync host traces prepared locally: target={targetPlayerId}, tick={tick}, diffAt={diffAt}, " +
                    $"traceBytes={packet.rawTraces.Length}, jittedBytes={packet.rawJittedMethods.Length}");
                Multiplayer.Client?.SendFragmented(packet.Serialize());
            }
            catch (Exception e)
            {
                Log.Error($"Exception preparing local host desync traces: {e}");
            }
        }

        protected override void SendRaw(byte[] raw, bool reliable = true)
        {
            enqueue(() =>
            {
                try
                {
                    remoteConn.HandleReceiveRaw(new ByteReader(raw), reliable);
                }
                catch (Exception e)
                {
                    Log.Error($"Exception handling packet by {remoteConn}: {e}");
                }
            });
        }

        protected override void OnClose(ServerDisconnectPacket? goodbye)
        {
        }

        public override string ToString() => isClient ? "LocalClientConn" : "LocalServerConn";
    }
}
