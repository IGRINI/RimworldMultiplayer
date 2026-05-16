using System.Buffers;
using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

public class ConnectionBaseSendTest
{
    [Test]
    public void DefaultLengthAwareSendRawKeepsDeferredCopyOutOfArrayPool()
    {
        var conn = new RetainingConnection();
        var payload = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();

        conn.Send(new ClientCommandPacket(CommandType.Sync, 123, payload));

        conn.RetainedRaw.Should().NotBeNull();
        var retained = conn.RetainedRaw!;
        var expected = retained.ToArray();

        TryPoisonIfReturnedToPool(retained);

        retained.Should().Equal(expected,
            "deferred transports must not retain a buffer returned to ArrayPool by ConnectionBase.Send");
    }

    private static void TryPoisonIfReturnedToPool(byte[] retained)
    {
        for (int i = 0; i < 1024; i++)
        {
            var rented = ArrayPool<byte>.Shared.Rent(retained.Length);
            if (ReferenceEquals(rented, retained))
            {
                Array.Fill(rented, (byte)0xCC);
                ArrayPool<byte>.Shared.Return(rented);
                return;
            }

            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private sealed class RetainingConnection : ConnectionBase
    {
        public byte[]? RetainedRaw { get; private set; }

        protected override void SendRaw(byte[] raw, bool reliable = true)
        {
            RetainedRaw = raw;
        }

        protected override void OnClose(ServerDisconnectPacket? goodbye)
        {
        }
    }
}
