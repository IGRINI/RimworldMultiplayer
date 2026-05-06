using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// CanonicalFingerprint.Compute() reads Find.Maps / Find.FactionManager / Multiplayer.AsyncWorldTime
// — all of which require a live RimWorld game world. The Tests project deliberately does NOT
// reference the RimWorld DLLs (only Common.csproj), so a true Compute() roundtrip test would
// need a heavyweight RimWorld test harness we don't have. The wire-format roundtrip below
// covers the network bit; the Compute() determinism guarantee is exercised in-game.
[TestFixture]
public class CanonicalFingerprintTest
{
    [Test]
    public void SyncOpinion_CanonicalFingerprint_RoundtripsAcrossWire()
    {
        // The whole point of the fingerprint is that two peers can compare ulong-equal values
        // off the wire. If serialization silently truncated to 32 bits or byte-swapped, every
        // peer would see "fingerprint mismatch" on every legitimate sync. Bake a known value
        // and verify bit-for-bit roundtrip.
        const ulong probe = 0xDEADBEEFCAFEBABEUL;

        var opinion = new SyncOpinion
        {
            startTick = 7,
            commandRandomStates = new() { 1u },
            worldRandomStates = new() { 2u },
            mapRandomStates = new()
            {
                new MapRandomState { mapId = 0, randomStates = new() { 3u } }
            },
            traceHashes = new() { 4 },
            simulating = false,
            roundMode = RoundModeEnum.ToNearest,
            canonicalFingerprint = probe
        };

        // Roundtrip the SyncOpinion struct through its Bind() method — same path the
        // SyncOpinionBinder in ClientSyncInfoPacket / ServerSyncInfoPacket uses.
        var writer = new ByteWriter();
        opinion.Bind(new PacketWriter(writer));
        var bytes = writer.ToArray();

        var reader = new ByteReader(bytes);
        var roundTripped = default(SyncOpinion);
        roundTripped.Bind(new PacketReader(reader));

        roundTripped.canonicalFingerprint.Should().Be(probe);
        roundTripped.startTick.Should().Be(7);
        roundTripped.roundMode.Should().Be(RoundModeEnum.ToNearest);
    }

    [Test]
    public void SyncOpinion_DefaultFingerprintRoundtripsAsZero()
    {
        // A peer whose CanonicalFingerprint.Compute() threw will ship a zero — the receiving
        // side must preserve that exact value so CheckForDesync's "treat zero as not-available"
        // shortcut continues to work and doesn't false-positive.
        var opinion = new SyncOpinion
        {
            startTick = 0,
            commandRandomStates = new(),
            worldRandomStates = new(),
            mapRandomStates = new(),
            traceHashes = new(),
            simulating = false,
            roundMode = RoundModeEnum.ToNearest
        };

        var writer = new ByteWriter();
        opinion.Bind(new PacketWriter(writer));
        var bytes = writer.ToArray();

        var reader = new ByteReader(bytes);
        var roundTripped = default(SyncOpinion);
        roundTripped.Bind(new PacketReader(reader));

        roundTripped.canonicalFingerprint.Should().Be(0UL);
    }
}
