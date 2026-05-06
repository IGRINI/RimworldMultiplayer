using Multiplayer.Common;

namespace Tests;

public class ConstructorCacheTest
{
    private class Sample
    {
        public int Value { get; set; }
    }

    private class WithNonPublicCtor
    {
        public int Value { get; }

        private WithNonPublicCtor()
        {
            Value = 42;
        }
    }

    private struct SampleStruct
    {
#pragma warning disable CS0649 // Field is never assigned to; default-initialized via parameterless ctor
        public int Value;
#pragma warning restore CS0649
    }

    [Test]
    public void ConstructorCache_CreatesInstance()
    {
        var instance = ConstructorCache.CreateInstance(typeof(Sample));

        Assert.That(instance, Is.Not.Null);
        Assert.That(instance, Is.InstanceOf<Sample>());
    }

    [Test]
    public void ConstructorCache_ReturnsSameDelegate()
    {
        var first = ConstructorCache.GetOrAddFactory(typeof(Sample));
        var second = ConstructorCache.GetOrAddFactory(typeof(Sample));

        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void ConstructorCache_HandlesNonPublicConstructor()
    {
        var instance = ConstructorCache.CreateInstance(typeof(WithNonPublicCtor));

        Assert.That(instance, Is.InstanceOf<WithNonPublicCtor>());
        Assert.That(((WithNonPublicCtor)instance).Value, Is.EqualTo(42));
    }

    [Test]
    public void ConstructorCache_HandlesValueType()
    {
        var instance = ConstructorCache.CreateInstance(typeof(SampleStruct));

        Assert.That(instance, Is.InstanceOf<SampleStruct>());
        Assert.That(((SampleStruct)instance).Value, Is.EqualTo(0));
    }
}
