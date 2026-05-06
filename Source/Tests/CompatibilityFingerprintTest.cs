using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Multiplayer.Common.Util;

namespace Tests;

// CompatibilityFingerprint folds the entire join-time compat check (mods, files, configs,
// versions) into a single ulong. The properties we actually rely on:
//   - same input → same output (deterministic)
//   - reordering inputs that are semantically unordered (file traversal order, config order)
//     must NOT change the hash — otherwise the fingerprint flaps on filesystem state
//   - reordering the mod LIST (which IS ordered, because load order matters) MUST change it
//   - changing any input must change the hash
[TestFixture]
public class CompatibilityFingerprintTest
{
    private static IEnumerable<(string, string)> Mods(params string[] ids)
        => ids.Select(id => (id, "ModsFolder"));

    private static IEnumerable<(string, string, long)> Files(params (string mod, string path, long hash)[] entries)
        => entries.Select(e => (e.mod, e.path, e.hash));

    private static IEnumerable<(string, string, string)> Configs(params (string mod, string file, string contents)[] entries)
        => entries.Select(e => (e.mod, e.file, e.contents));

    [Test]
    public void Compute_SameInput_SameFingerprint()
    {
        var fp1 = CompatibilityFingerprint.Compute(
            Mods("a", "b"),
            Files(("a", "x.dll", 1), ("b", "y.xml", 2)),
            Configs(("a", "s.xml", "<s/>")),
            "1.6", "0.11");
        var fp2 = CompatibilityFingerprint.Compute(
            Mods("a", "b"),
            Files(("a", "x.dll", 1), ("b", "y.xml", 2)),
            Configs(("a", "s.xml", "<s/>")),
            "1.6", "0.11");

        fp1.Should().Be(fp2);
    }

    [Test]
    public void Compute_FileTraversalOrderDoesNotMatter()
    {
        // Insertion order into ModFileDict must NOT affect the fingerprint — files come
        // from filesystem walks whose order depends on the OS, not on user intent.
        var fp1 = CompatibilityFingerprint.Compute(
            Mods("a"),
            Files(("a", "x.dll", 1), ("a", "y.xml", 2), ("a", "z.xml", 3)),
            Configs(),
            "1.6", "0.11");
        var fp2 = CompatibilityFingerprint.Compute(
            Mods("a"),
            Files(("a", "z.xml", 3), ("a", "x.dll", 1), ("a", "y.xml", 2)),
            Configs(),
            "1.6", "0.11");

        fp1.Should().Be(fp2);
    }

    [Test]
    public void Compute_ConfigOrderDoesNotMatter()
    {
        var fp1 = CompatibilityFingerprint.Compute(
            Mods("a"), Files(),
            Configs(("a", "s1.xml", "<s/>"), ("a", "s2.xml", "<t/>")),
            "1.6", "0.11");
        var fp2 = CompatibilityFingerprint.Compute(
            Mods("a"), Files(),
            Configs(("a", "s2.xml", "<t/>"), ("a", "s1.xml", "<s/>")),
            "1.6", "0.11");

        fp1.Should().Be(fp2);
    }

    [Test]
    public void Compute_ModListOrderMatters()
    {
        // Load order is meaningful — reordered mods is a different setup, must produce a
        // different fingerprint.
        var fp1 = CompatibilityFingerprint.Compute(Mods("a", "b"), Files(), Configs(), "1.6", "0.11");
        var fp2 = CompatibilityFingerprint.Compute(Mods("b", "a"), Files(), Configs(), "1.6", "0.11");

        fp1.Should().NotBe(fp2);
    }

    [Test]
    public void Compute_AnyInputChangeChangesFingerprint()
    {
        var baseline = CompatibilityFingerprint.Compute(
            Mods("a"), Files(("a", "x.xml", 1)), Configs(("a", "s.xml", "<s/>")), "1.6", "0.11");

        // RW version
        CompatibilityFingerprint.Compute(
            Mods("a"), Files(("a", "x.xml", 1)), Configs(("a", "s.xml", "<s/>")), "1.7", "0.11")
            .Should().NotBe(baseline);

        // MP version
        CompatibilityFingerprint.Compute(
            Mods("a"), Files(("a", "x.xml", 1)), Configs(("a", "s.xml", "<s/>")), "1.6", "0.12")
            .Should().NotBe(baseline);

        // Mod list
        CompatibilityFingerprint.Compute(
            Mods("a", "b"), Files(("a", "x.xml", 1)), Configs(("a", "s.xml", "<s/>")), "1.6", "0.11")
            .Should().NotBe(baseline);

        // File hash
        CompatibilityFingerprint.Compute(
            Mods("a"), Files(("a", "x.xml", 99)), Configs(("a", "s.xml", "<s/>")), "1.6", "0.11")
            .Should().NotBe(baseline);

        // File path
        CompatibilityFingerprint.Compute(
            Mods("a"), Files(("a", "y.xml", 1)), Configs(("a", "s.xml", "<s/>")), "1.6", "0.11")
            .Should().NotBe(baseline);

        // Config contents
        CompatibilityFingerprint.Compute(
            Mods("a"), Files(("a", "x.xml", 1)), Configs(("a", "s.xml", "<t/>")), "1.6", "0.11")
            .Should().NotBe(baseline);
    }

    [Test]
    public void Compute_NullInputsTreatedAsEmpty()
    {
        // Production code can pass null when configs aren't synced. Don't NRE.
        var fp1 = CompatibilityFingerprint.Compute(Mods("a"), Files(), null!, "1.6", "0.11");
        var fp2 = CompatibilityFingerprint.Compute(Mods("a"), Files(), Configs(), "1.6", "0.11");
        fp1.Should().Be(fp2);
    }
}
