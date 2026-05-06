using FluentAssertions;
using Multiplayer.Common.Util;

namespace Tests;

// StableHash backs the desync trace path (and any future cross-process hash). The whole
// point is determinism: same input must produce same output across processes, .NET versions,
// and machine reboots. We can't write a unit test that spans processes, but we CAN bake the
// known-good FNV-1a 64 outputs as constants — that catches accidental algorithm drift, which
// is the actual failure mode this guards against.
[TestFixture]
public class StableHashTest
{
    [Test]
    public void String_IsDeterministicAcrossCalls()
    {
        StableHash.String("rimworld").Should().Be(StableHash.String("rimworld"));
        StableHash.String("").Should().Be(StableHash.String(""));
    }

    [Test]
    public void String_NullReturnsOffsetBasis()
    {
        // Null must produce a stable, well-defined value (not throw, not UB).
        StableHash.String(null!).Should().Be(0xCBF29CE484222325UL);
    }

    [Test]
    public void String_ProducesKnownFnv1aValues()
    {
        // Reference FNV-1a 64 over UTF-16 LE code units. If these constants change, the
        // hash algorithm changed — every existing replay/desync log will mismatch peers
        // running an older build. Touching these requires a protocol-version bump.
        StableHash.String("").Should().Be(0xCBF29CE484222325UL);
        StableHash.String("abc").Should().Be(0xCEC64E155111225DUL);
        StableHash.String("hello world").Should().Be(0xF7F81A3F5BD66DDDUL);
    }

    [Test]
    public void StringInt32_TruncatesUlongConsistently()
    {
        // The desync trace path stores int32 hashes on the wire. StringInt32 must agree
        // with the low 32 bits of String() — otherwise mixing the two APIs in the same
        // pipeline would silently desync.
        StableHash.StringInt32("abc").Should().Be(unchecked((int)0x5111225DU));
        StableHash.StringInt32("hello world").Should().Be(unchecked((int)0x5BD66DDDU));
    }

    [Test]
    public void Combine_IsDeterministic()
    {
        var a = StableHash.String("foo");
        var b = StableHash.String("bar");
        StableHash.Combine(a, b).Should().Be(StableHash.Combine(a, b));
    }

    [Test]
    public void Combine_DistinguishesDifferentInputs()
    {
        // Different pairs must produce different combined hashes (collision-resistance
        // guarantee, modulo unavoidable 64-bit collisions). XOR-then-multiply is
        // commutative on its inputs, but the Combine result still differs across input
        // sets — protect against accidental simplification to e.g. plain XOR.
        var a = StableHash.String("foo");
        var b = StableHash.String("bar");
        var c = StableHash.String("baz");
        StableHash.Combine(a, b).Should().NotBe(StableHash.Combine(a, c));
    }
}
