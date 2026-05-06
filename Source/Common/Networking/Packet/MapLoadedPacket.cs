namespace Multiplayer.Common.Networking.Packet;

/// Sent by the client after Loader.ReloadGame finishes and the map is in Find.Maps and
/// dispatchable by TickPatch.TickableById. The server uses this to flush pendingMapCmds for
/// the player onto the wire, and to add the map to the player's loadedMapIds so subsequent
/// cmds for that map are sent eagerly.
[PacketDefinition(Packets.Client_MapLoaded)]
public record struct ClientMapLoadedPacket(int mapId) : IPacket
{
    public int mapId = mapId;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapId);
    }
}
