using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Multiplayer.Common.Util;

// Re-emit XML in a canonical form so cosmetic differences (formatting, comments, attribute
// order, line endings, whitespace) don't surface as mismatches when two clients compare
// config text. Two XML files that mean the same thing should compare equal.
//
// SCOPE: XML only. Mod settings in TOML/JSON/plaintext fall through unchanged — accepting
// false-positive mismatches for those formats is the lesser evil vs guessing format from
// contents and getting it wrong.
public static class XmlNormalize
{
    public static string Normalize(string contents)
    {
        if (string.IsNullOrEmpty(contents)) return contents;

        // Cheap pre-check: if it doesn't start with '<' (after BOM / whitespace) it's not XML
        // and we shouldn't even ask XDocument to parse it. Avoids the XmlException-allocation
        // path for every TOML/JSON/plaintext config that flows through here.
        var firstNonWs = -1;
        for (int i = 0; i < contents.Length; i++)
        {
            var c = contents[i];
            if (c == '﻿' || char.IsWhiteSpace(c)) continue;
            firstNonWs = i;
            break;
        }
        if (firstNonWs < 0 || contents[firstNonWs] != '<') return contents;

        try
        {
            var doc = XDocument.Parse(contents, LoadOptions.None);
            StripComments(doc);
            SortAttributes(doc);
            // SaveOptions.DisableFormatting → no insignificant whitespace, no indentation —
            // single canonical line per logical run. The XDeclaration round-trips automatically.
            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            // Malformed XML — return as-is; caller doesn't care about the reason. Guessing the
            // intended canonical form of broken markup is worse than leaving the false positive.
            return contents;
        }
    }

    private static void StripComments(XDocument doc)
    {
        // Walk once, collect to a list, then remove. Removing during traversal would mutate
        // the iterator we're walking.
        var toRemove = new List<XNode>();
        foreach (var node in doc.DescendantNodes())
            if (node is XComment) toRemove.Add(node);

        foreach (var n in toRemove) n.Remove();
    }

    private static void SortAttributes(XDocument doc)
    {
        // Attribute order is semantically irrelevant in XML but XDocument preserves source
        // order. Re-add in name-sorted order so two semantically-equal files with attributes
        // in different order produce the same canonical text.
        foreach (var el in doc.Descendants())
        {
            var attrs = el.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList();
            if (attrs.Count <= 1) continue;
            el.RemoveAttributes();
            foreach (var a in attrs) el.Add(a);
        }
    }
}
