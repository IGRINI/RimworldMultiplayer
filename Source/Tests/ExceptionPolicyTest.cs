using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

// Section 10 of the architectural roadmap: split sim-command exceptions into "recoverable UI/log"
// vs "fatal simulation divergence". A Sync command can mutate state before throwing; logging-and-
// continuing leaves the local sim partially-applied while peers may have applied it cleanly →
// silent state drift. ExecuteCmd in AsyncTimeComp / AsyncWorldTimeComp now treats any non-fatal
// exception (i.e. anything except OOM/StackOverflow which are fatal anyway) as a protocol desync
// via Multiplayer.session.TriggerProtocolDesync.
//
// The full session-level test (mock connection sees exactly one ClientDesyncedPacket; second
// trigger is idempotent) cannot live in this project: MultiplayerSession is in Source/Client which
// targets net48 + Unity/RimWorld assemblies, while Tests targets net8.0 and references only
// Common. So the assertions below cover what IS reachable from Common: the packet contract that
// TriggerProtocolDesync emits. The session-level idempotency is enforced by the early-return
// `if (desynced) return;` in MultiplayerSession.TriggerProtocolDesync (verified by inspection).
[TestFixture]
public class ExceptionPolicyTest
{
    [Test]
    public void ClientDesyncedPacket_RoundtripsThroughByteIO()
    {
        // Sanity-check the packet TriggerProtocolDesync sends — if this ever drifts, the protocol
        // desync path silently sends a malformed packet. Mirrors how the wire layer binds packets:
        // PacketWriter for serialize, PacketReader for deserialize.
        var sent = new ClientDesyncedPacket(tick: 12345, diffAt: 0);

        var writer = new ByteWriter();
        sent.Bind(new PacketWriter(writer));

        var reader = new ByteReader(writer.ToArray());
        var read = new ClientDesyncedPacket();
        read.Bind(new PacketReader(reader));

        read.tick.Should().Be(12345);
        read.diffAt.Should().Be(0);
    }
}
