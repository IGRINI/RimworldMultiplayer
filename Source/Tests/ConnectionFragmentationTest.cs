using System.Reflection;
using FluentAssertions;
using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class ConnectionFragmentationTest
{
    private static readonly MethodInfo HandleReceiveFragmentMethod =
        typeof(ConnectionBase).GetMethod("HandleReceiveFragment", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(nameof(ConnectionBase), "HandleReceiveFragment");

    [Test]
    public void AcceptsTwoInterleavedFragmentedPackets()
    {
        var sender = new RawRecordingConnection("sender");
        byte[] firstPayload = CreatePayload(3_000, 11);
        byte[] secondPayload = CreatePayload(3_000, 29);

        sender.SendFragmented(Packets.Server_WorldData, firstPayload);
        sender.SendFragmented(Packets.Server_WorldData, secondPayload);

        byte firstFragmentId = sender.RawPackets[0][1];
        byte secondFragmentId = sender.RawPackets.First(packet => packet[1] != firstFragmentId)[1];
        var firstFragments = FragmentsWithId(sender.RawPackets, firstFragmentId);
        var secondFragments = FragmentsWithId(sender.RawPackets, secondFragmentId);
        firstFragments.Should().HaveCountGreaterThan(1);
        secondFragments.Should().HaveCountGreaterThan(1);

        var receiver = new DummyConnection("receiver");
        var received = new List<byte[]>();
        var handler = new PacketHandlerInfo(
            (_, reader) => received.Add(reader.ReadRaw(reader.Left)),
            Fragment: true);

        ReceiveFragment(receiver, firstFragments[0], handler);
        ReceiveFragment(receiver, secondFragments[0], handler);
        foreach (byte[] fragment in firstFragments.Skip(1))
            ReceiveFragment(receiver, fragment, handler);
        foreach (byte[] fragment in secondFragments.Skip(1))
            ReceiveFragment(receiver, fragment, handler);

        received.Should().HaveCount(2);
        received[0].Should().Equal(firstPayload);
        received[1].Should().Equal(secondPayload);
    }

    [Test]
    public void ChangeStateClearsIncompleteFragmentedPackets()
    {
        var firstSender = new RawRecordingConnection("first");
        var secondSender = new RawRecordingConnection("second");

        firstSender.SendFragmented(Packets.Server_HostSaveTransfer, CreatePayload(3_000, 11));
        secondSender.SendFragmented(Packets.Server_SyncInfo, CreatePayload(3_000, 29));

        firstSender.RawPackets[0][1].Should().Be(secondSender.RawPackets[0][1],
            "fresh connections start fragment ids from the same value");

        var receiver = new DummyConnection("receiver");
        var handler = new PacketHandlerInfo((_, _) => { }, Fragment: true);

        ReceiveFragment(receiver, firstSender.RawPackets[0], handler);
        receiver.ChangeState(ConnectionStateEnum.Disconnected);

        Action receiveAfterStateChange = () => ReceiveFragment(receiver, secondSender.RawPackets[0], handler);
        receiveAfterStateChange.Should().NotThrow();
    }

    private static byte[] CreatePayload(int size, int seed)
    {
        var data = new byte[size];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)((i + seed) % byte.MaxValue);

        return data;
    }

    private static List<byte[]> FragmentsWithId(IEnumerable<byte[]> rawPackets, byte fragmentId) =>
        rawPackets.Where(packet => packet[1] == fragmentId).ToList();

    private static void ReceiveFragment(ConnectionBase connection, byte[] raw, PacketHandlerInfo handler)
    {
        var packetType = (Packets)(raw[0] & 0x3F);
        var body = raw.Skip(1).ToArray();
        HandleReceiveFragmentMethod.Invoke(connection, [new ByteReader(body), packetType, handler]);
    }

    private sealed class RawRecordingConnection : ConnectionBase
    {
        public List<byte[]> RawPackets { get; } = [];

        public RawRecordingConnection(string username)
        {
            this.username = username;
        }

        public override int Latency { get => 0; set { } }

        protected override void SendRaw(byte[] raw, bool reliable) => RawPackets.Add(raw.ToArray());

        protected override void SendRaw(byte[] raw, int length, bool reliable)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(raw, 0, copy, 0, length);
            RawPackets.Add(copy);
        }

        protected override void OnClose(Multiplayer.Common.Networking.Packet.ServerDisconnectPacket? goodbye)
        {
        }
    }
}
