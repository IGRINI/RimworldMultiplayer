using Multiplayer.Client.Networking;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Client;

public abstract class ClientBaseState(ConnectionBase connection) : MpConnectionState(connection)
{
    protected MultiplayerSession Session => Multiplayer.session;

    [TypedPacketHandler]
    public void HandleKeepAlive(ServerKeepAlivePacket packet)
    {
        int ticksBehind = TickPatch.tickUntil - TickPatch.Timer;

        connection.Send(new ClientKeepAlivePacket(packet.id, ticksBehind, TickPatch.Simulating, TickPatch.workTicks),
            false);
    }

    [TypedPacketHandler]
    public void HandleTimeControl(ServerTimeControlPacket packet)
    {
        // Both fields advance independently. The old guard (`remoteTickUntil >= packet.tickUntil
        // return`) silently dropped sentCmds updates whenever the tick window hadn't moved, which
        // could leave ProcessTimeControl gated on a stale-low remoteSentCmds and freeze the sim
        // even though the server had emitted more cmds. Update each monotonically so neither can
        // regress.
        TickPatch.serverTimePerTick = packet.serverTimePerTick;
        if (packet.tickUntil > Multiplayer.session.remoteTickUntil)
            Multiplayer.session.remoteTickUntil = packet.tickUntil;
        if (packet.sentCmds > Multiplayer.session.remoteSentCmds)
            Multiplayer.session.remoteSentCmds = packet.sentCmds;
        Multiplayer.session.ProcessTimeControl();
    }

    // Currently handles disconnection only for Steam connections. See comment in ConnectionBase.Close for more info.
    [TypedPacketHandler]
    public virtual void HandleDisconnected(ServerDisconnectPacket packet)
    {
        ConnectionStatusListeners.TryNotifyAll_Disconnected(SessionDisconnectInfo.From(packet));
        Multiplayer.StopMultiplayer();
    }
}
