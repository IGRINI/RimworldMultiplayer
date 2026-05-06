using System;
using System.Collections.Generic;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

public class RecordingConnection : ConnectionBase
{
    public List<Packets> SentPackets { get; } = new();
    // Full sent messages with the packet id stripped from byte[0]. Useful for tests that need to
    // assert on packet body — e.g. WorldData snapshot contents, Server_Command payloads buffered
    // and drained on Client_MapLoaded ack. Fragmented packets land here as their individual fragments.
    public List<(Packets id, byte[] body)> SentMessages { get; } = new();

    public RecordingConnection(string username)
    {
        this.username = username;
    }

    public override int Latency { get => 0; set { } }

    protected override void SendRaw(byte[] raw, bool reliable) => SendRaw(raw, raw.Length, reliable);

    protected override void SendRaw(byte[] raw, int length, bool reliable)
    {
        if (length == 0)
            return;

        var id = (Packets)(raw[0] & 0x3F);
        SentPackets.Add(id);

        var body = new byte[length - 1];
        if (body.Length > 0)
            Buffer.BlockCopy(raw, 1, body, 0, body.Length);
        SentMessages.Add((id, body));
    }

    protected override void OnClose(ServerDisconnectPacket? goodbye) { }
}