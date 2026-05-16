namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_Traces, allowFragmented: true)]
public record struct ClientTracesPacket : IPacket
{
    // Compressed trace/jitted-method blobs regularly exceed PacketBuffer's default 32 KiB
    // payload limit on large mod lists. The outer packet is already fragmented and bounded by
    // ConnectionBase.MaxFragmentPacketTotalSize, so keep a generous per-blob cap here.
    public const int MaxTraceBlobLength = 16 * 1024 * 1024;

    public int playerId;
    public byte[] rawTraces;
    public byte[] rawJittedMethods;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref playerId);
        buf.BindBytes(ref rawTraces, MaxTraceBlobLength);
        buf.BindBytes(ref rawJittedMethods, MaxTraceBlobLength);
    }
}

[PacketDefinition(Packets.Server_Traces, allowFragmented: true)]
public record struct ServerTracesPacket : IPacket
{
    public enum Mode : byte
    {
        Request, Transfer
    }

    public Mode mode;

    public int tick;
    public int diffAt;
    public int playerId;

    // Used in transfer only
    public byte[] rawTraces;
    public byte[] rawJittedMethods;

    public static ServerTracesPacket Request(int tick, int diffAt, int playerId) => new()
    {
        mode = Mode.Request,
        tick = tick,
        diffAt = diffAt,
        playerId = playerId
    };

    public static ServerTracesPacket Transfer(byte[] rawTraces, byte[] rawJittedMethods) =>
        new() { mode = Mode.Transfer, rawTraces = rawTraces, rawJittedMethods = rawJittedMethods };

    public void Bind(PacketBuffer buf)
    {
        buf.BindEnum(ref mode);
        if (mode == Mode.Request)
        {
            buf.Bind(ref tick);
            buf.Bind(ref diffAt);
            buf.Bind(ref playerId);
        }
        else
        {
            buf.BindBytes(ref rawTraces, ClientTracesPacket.MaxTraceBlobLength);
            buf.BindBytes(ref rawJittedMethods, ClientTracesPacket.MaxTraceBlobLength);
        }
    }
}
