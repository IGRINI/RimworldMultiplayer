namespace Multiplayer.Common.Networking.Packet;

// Section 8 (architectural roadmap): paired desync save. The desynced client asks the server for
// the host's most recent worldData snapshot so the desync zip contains both the LOCAL and the
// HOST reference state. This is the simpler-scope implementation: we ship the cached
// WorldData.savedGame blob (the last join-point upload), not a fresh on-demand host save. That
// snapshot is potentially many ticks behind "now", but it's already cached server-side, free to
// produce, and a meaningful reference for post-mortem diff. A future enhancement could ask the
// host to produce a fresh save on demand, but cross-process Save+upload would block the host's
// main thread and bloat the protocol with a state machine — not worth it for diagnostics.

[PacketDefinition(Packets.Client_RequestHostSave)]
public record struct ClientRequestHostSavePacket : IPacket
{
    public void Bind(PacketBuffer buf) { }
}

[PacketDefinition(Packets.Server_HostSaveTransfer, allowFragmented: true)]
public record struct ServerHostSaveTransferPacket : IPacket
{
    // GZipped RimWorld savegame XML. May be empty if the server has no savedGame yet (initial
    // host with no join points uploaded), in which case the client writes a marker file instead
    // of the .rws.
    public byte[] rawSavedGame;

    public ServerHostSaveTransferPacket(byte[] rawSavedGame)
    {
        this.rawSavedGame = rawSavedGame;
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindBytes(ref rawSavedGame, maxLength: -1);
    }
}
