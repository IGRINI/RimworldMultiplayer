using FluentAssertions;
using Multiplayer.Common.Util;

namespace Tests;

// XmlNormalize collapses cosmetic differences in mod-config XML so two clients with the
// same semantic settings compare string-equal. The properties we rely on:
//   - whitespace differences are erased
//   - comments are stripped
//   - attribute order is canonicalised
//   - non-XML input is returned unchanged (we don't break TOML/JSON/plaintext settings)
//   - invalid XML is returned unchanged (don't blow up trying to canonicalise broken markup)
[TestFixture]
public class XmlNormalizeTest
{
    [Test]
    public void Normalize_WhitespaceDifference_SameOutput()
    {
        var a = "<root>  <child>v</child>  </root>";
        var b = "<root><child>v</child></root>";
        XmlNormalize.Normalize(a).Should().Be(XmlNormalize.Normalize(b));
    }

    [Test]
    public void Normalize_LineEndingDifference_SameOutput()
    {
        var a = "<root>\n  <child>v</child>\n</root>";
        var b = "<root>\r\n  <child>v</child>\r\n</root>";
        XmlNormalize.Normalize(a).Should().Be(XmlNormalize.Normalize(b));
    }

    [Test]
    public void Normalize_CommentDifference_SameOutput()
    {
        var a = "<root><!-- a comment --><child>v</child></root>";
        var b = "<root><child>v</child></root>";
        XmlNormalize.Normalize(a).Should().Be(XmlNormalize.Normalize(b));
    }

    [Test]
    public void Normalize_AttributeReorder_SameOutput()
    {
        var a = "<root><child a=\"1\" b=\"2\"/></root>";
        var b = "<root><child b=\"2\" a=\"1\"/></root>";
        XmlNormalize.Normalize(a).Should().Be(XmlNormalize.Normalize(b));
    }

    [Test]
    public void Normalize_DifferentSemantic_DifferentOutput()
    {
        // Sanity check the negative case — the canonicalisation must not be so aggressive
        // that it loses real differences.
        var a = "<root><child>v1</child></root>";
        var b = "<root><child>v2</child></root>";
        XmlNormalize.Normalize(a).Should().NotBe(XmlNormalize.Normalize(b));
    }

    [Test]
    public void Normalize_NonXmlInput_ReturnedUnchanged()
    {
        // TOML, JSON, plaintext — we don't touch any of them. Better to accept whitespace
        // false positives for those formats than to mangle them with an XML parser.
        var toml = "name = \"value\"\nkey = 42";
        XmlNormalize.Normalize(toml).Should().Be(toml);

        var json = "{\"k\":\"v\"}";
        XmlNormalize.Normalize(json).Should().Be(json);

        var plain = "just some text";
        XmlNormalize.Normalize(plain).Should().Be(plain);
    }

    [Test]
    public void Normalize_MalformedXml_ReturnedUnchanged()
    {
        // Looks like XML (starts with '<') but doesn't parse. Don't throw, don't try to
        // guess what was meant — just hand it back.
        var broken = "<root><unclosed>";
        XmlNormalize.Normalize(broken).Should().Be(broken);
    }

    [Test]
    public void Normalize_NullOrEmpty_ReturnedUnchanged()
    {
        XmlNormalize.Normalize(null!).Should().BeNull();
        XmlNormalize.Normalize("").Should().Be("");
    }

    [Test]
    public void Normalize_LeadingWhitespace_StillRecognisedAsXml()
    {
        // Real-world configs often have a leading newline / BOM. Make sure those still
        // route into the XML normaliser instead of falling through as "not XML".
        var a = "\n\n<root><child>v</child></root>";
        var b = "<root><child>v</child></root>";
        XmlNormalize.Normalize(a).Should().Be(XmlNormalize.Normalize(b));
    }
}
