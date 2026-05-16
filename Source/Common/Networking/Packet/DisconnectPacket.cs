namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Server_Disconnect)]
public record struct ServerDisconnectPacket : IPacket
{
    public MpDisconnectReason reason;
    public byte[] data;

    public void Bind(PacketBuffer buf)
    {
        buf.BindEnum(ref reason);
        if (buf.isWriting)
            data ??= [];
        buf.BindRemaining(ref data);
    }
}
