using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

public class PacketReaderValidationTest
{
    [Test]
    public void BindBytes_RejectsNegativeLength()
    {
        var reader = ReaderWith(w => w.WriteInt32(-1));
        byte[] dst = null!;

        Action act = () => reader.BindBytes(ref dst);

        act.Should().Throw<ReaderException>().WithMessage("*<0*");
    }

    [Test]
    public void BindBytes_RejectsLengthAboveMaxLength()
    {
        var reader = ReaderWith(w =>
        {
            w.WriteInt32(100);
            w.WriteRaw(new byte[100]);
        });
        byte[] dst = null!;

        Action act = () => reader.BindBytes(ref dst, maxLength: 50);

        act.Should().Throw<ReaderException>().WithMessage("*100>50*");
    }

    [Test]
    public void BindBytes_AcceptsAnyLengthWhenMaxLengthIsUnlimited()
    {
        var payload = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var reader = ReaderWith(w =>
        {
            w.WriteInt32(payload.Length);
            w.WriteRaw(payload);
        });
        byte[] dst = null!;

        reader.BindBytes(ref dst, maxLength: -1);

        dst.Should().Equal(payload);
    }

    [Test]
    public void BindBytes_RejectsLengthAboveRemainingBuffer()
    {
        var reader = ReaderWith(w =>
        {
            w.WriteInt32(1_000_000);
            w.WriteRaw(new byte[10]); // claim 1M, ship 10
        });
        byte[] dst = null!;

        Action act = () => reader.BindBytes(ref dst, maxLength: -1);

        act.Should().Throw<ReaderException>().WithMessage("*remaining*");
    }

    [Test]
    public void BindArray_RejectsNegativeLength()
    {
        var reader = ReaderWith(w => w.WriteInt32(-1));
        int[] dst = null!;

        Action act = () => reader.Bind(ref dst, BinderOf.Int());

        act.Should().Throw<ReaderException>().WithMessage("*<0*");
    }

    [Test]
    public void BindList_RejectsNegativeLength()
    {
        var reader = ReaderWith(w => w.WriteInt32(-1));
        List<int> dst = null!;

        Action act = () => reader.Bind(ref dst, BinderOf.Int());

        act.Should().Throw<ReaderException>().WithMessage("*<0*");
    }

    private static PacketReader ReaderWith(Action<ByteWriter> write)
    {
        var w = new ByteWriter();
        write(w);
        return new PacketReader(new ByteReader(w.ToArray()));
    }
}
