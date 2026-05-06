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

    [Test]
    public void ConstructorCache_CreateList_ReturnsTypedListWithCapacity()
    {
        var list = ConstructorCache.CreateList(typeof(int), 7);

        Assert.That(list, Is.InstanceOf<List<int>>());
        Assert.That(((List<int>)list).Capacity, Is.GreaterThanOrEqualTo(7));
        Assert.That(((List<int>)list).Count, Is.EqualTo(0));
    }

    [Test]
    public void ConstructorCache_CreateList_WorksForReferenceElement()
    {
        var list = (List<string>)ConstructorCache.CreateList(typeof(string), 3);
        list.Add("a");
        list.Add("b");

        Assert.That(list, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void ConstructorCache_CreateHashSet_CopiesEnumerable()
    {
        var src = new List<int> { 1, 2, 2, 3 };

        var set = (HashSet<int>)ConstructorCache.CreateHashSet(typeof(int), src);

        Assert.That(set, Is.EquivalentTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void ConstructorCache_CreateHashSet_WorksForReferenceElement()
    {
        var src = new List<string> { "x", "y" };

        var set = (HashSet<string>)ConstructorCache.CreateHashSet(typeof(string), src);

        Assert.That(set, Is.EquivalentTo(new[] { "x", "y" }));
    }
}
