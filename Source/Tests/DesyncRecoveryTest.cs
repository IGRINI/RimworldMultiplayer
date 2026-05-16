using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// Section 8 of the architectural roadmap: desync recovery + diagnostic improvements. Most of the
// surface — MpSettings.autoRejoinOnDesync, MpSettings.heavyDiagnosticMode, the auto-rejoin
// scheduling in MultiplayerSession.TriggerProtocolDesync / SyncCoordinator.HandleDesync, and the
// SaveableDesyncInfo paired-save logic — lives in Source/Client which targets net48 + Unity/
// RimWorld assemblies. Tests targets net8.0 and references only Common, so those are unreachable
// from here. What we CAN cover is the on-wire contract of the new packet pair and the protocol
// version bump that goes with it. Same scope-by-construction story as ExceptionPolicyTest.
[TestFixture]
public class DesyncRecoveryTest
{
    [Test]
    public void Protocol_BumpedForNewPacketPair()
    {
        // Adding new packet ids without a protocol bump silently lets a vNext client connect to a
        // vCurrent server and then trip "unknown packet id 60" on the wire. The bump from 54 → 55
        // is the trip-wire that tells either side they're incompatible at handshake time. If this
        // test fails because someone added even more packets and bumped further, just update the
        // expected value.
        MpVersion.Protocol.Should().BeGreaterThanOrEqualTo(55);
    }

    [Test]
    public void ClientRequestHostSavePacket_HasEmptyBody()
    {
        // The request packet carries no data — desynced peer just signals "send me your cached
        // save". If a future change adds a field, the snapshot serialization in PacketTest will
        // also fail; this is a more pointed assertion for readers.
        var packet = new ClientRequestHostSavePacket();
        var writer = new ByteWriter();
        packet.Bind(new PacketWriter(writer));

        writer.ToArray().Should().BeEmpty();
    }

    [Test]
    public void ServerHostSaveTransferPacket_RoundtripsArbitraryBytes()
    {
        // The save is gzipped XML on the wire; the packet treats it as opaque bytes. Test with a
        // mix that includes nulls and high-bit bytes so any accidental encoding/length issue
        // surfaces.
        var payload = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0xFF, 0xFE, 0x42 };
        var sent = new ServerHostSaveTransferPacket(payload);

        var writer = new ByteWriter();
        sent.Bind(new PacketWriter(writer));

        var reader = new ByteReader(writer.ToArray());
        var read = new ServerHostSaveTransferPacket();
        read.Bind(new PacketReader(reader));

        read.rawSavedGame.Should().BeEquivalentTo(payload);
    }

    [Test]
    public void ServerHostSaveTransferPacket_RoundtripsEmptyPayload()
    {
        // Server replies with an empty array when no worldData snapshot is cached yet (early-
        // session desync before any join-point upload). Client side must accept this gracefully —
        // the packet itself must roundtrip an empty array without falling over on zero-length
        // bind.
        var sent = new ServerHostSaveTransferPacket(System.Array.Empty<byte>());

        var writer = new ByteWriter();
        sent.Bind(new PacketWriter(writer));

        var reader = new ByteReader(writer.ToArray());
        var read = new ServerHostSaveTransferPacket();
        read.Bind(new PacketReader(reader));

        read.rawSavedGame.Should().NotBeNull();
        read.rawSavedGame.Should().BeEmpty();
    }

    [Test]
    public void Packets_Enum_HasNewSection8Members()
    {
        // Catch accidental enum-reordering: the existing Client_MapLoaded must come before the
        // new entries, and the new entries must precede Count. Reordering would shift on-wire
        // ids and quietly break any peer that targets the older protocol number.
        ((byte)Packets.Client_MapLoaded).Should().BeLessThan((byte)Packets.Client_RequestHostSave);
        ((byte)Packets.Client_RequestHostSave).Should().BeLessThan((byte)Packets.Server_HostSaveTransfer);
        ((byte)Packets.Server_HostSaveTransfer).Should().BeLessThan((byte)Packets.Count);
    }

    [Test]
    public void ClientTracesPacket_AllowsTraceBlobsLargerThanDefaultPacketLimit()
    {
        var sent = new ClientTracesPacket
        {
            playerId = 7,
            rawTraces = MakePayload(40_000, 11),
            rawJittedMethods = MakePayload(45_000, 29)
        };

        var writer = new ByteWriter();
        sent.Bind(new PacketWriter(writer));

        var reader = new ByteReader(writer.ToArray());
        var read = new ClientTracesPacket();
        read.Bind(new PacketReader(reader));

        read.playerId.Should().Be(sent.playerId);
        read.rawTraces.Should().Equal(sent.rawTraces);
        read.rawJittedMethods.Should().Equal(sent.rawJittedMethods);
    }

    [Test]
    public void ServerTracesPacket_Transfer_AllowsTraceBlobsLargerThanDefaultPacketLimit()
    {
        var sent = ServerTracesPacket.Transfer(
            MakePayload(40_000, 13),
            MakePayload(45_000, 31));

        var writer = new ByteWriter();
        sent.Bind(new PacketWriter(writer));

        var reader = new ByteReader(writer.ToArray());
        var read = new ServerTracesPacket();
        read.Bind(new PacketReader(reader));

        read.mode.Should().Be(ServerTracesPacket.Mode.Transfer);
        read.rawTraces.Should().Equal(sent.rawTraces);
        read.rawJittedMethods.Should().Equal(sent.rawJittedMethods);
    }

    private static byte[] MakePayload(int length, int seed)
    {
        var payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)((i + seed) % byte.MaxValue);

        return payload;
    }
}
