namespace Multiplayer.Common.Networking.Packet;

/// Sent by the client after Loader.ReloadGame finishes and the map is in Find.Maps and
/// dispatchable by TickPatch.TickableById. The server uses this to flush pendingMapCmds for
/// the player onto the wire, and to add the map to the player's loadedMapIds so subsequent
/// cmds for that map are sent eagerly.
///
/// The transferId echoes the generation the server stamped on the matching Server_MapResponse
/// (ServerPlayer.mapTransferIds, bumped on every SendMapResponse for a given mapId). The
/// server-side handler discards any ack whose transferId doesn't match the current generation
/// — that drops stale acks from transfers superseded by a Rejoin or a re-stream.
[PacketDefinition(Packets.Client_MapLoaded)]
public record struct ClientMapLoadedPacket(int mapId, int transferId) : IPacket
{
    public int mapId = mapId;
    public int transferId = transferId;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapId);
        buf.Bind(ref transferId);
    }
}
