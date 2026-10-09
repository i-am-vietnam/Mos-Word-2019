using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        // Read each logical story paragraph once: an outer drawing paragraph must
        // not acquire its nested textbox text, and Choice/Fallback are alternatives.
        private static List<XElement> ScopedStoryParagraphs(XDocument document, string scope)
        {
            if (scope != "TextBox" && scope != "MainBody" && scope != "AllBodyStories")
                throw new InvalidDataException("Unsupported paragraph scope.");
            return document.Root.Element(W + "body").Descendants(W + "p")
                .Where(p => !p.Ancestors(Mc + "Fallback").Any(f => f.Parent.Elements(Mc + "Choice")
                    .Any(c => c.Descendants(W + "txbxContent").Any())))
                .Where(p => scope == "AllBodyStories" ||
                    p.Ancestors(W + "txbxContent").Any() == (scope == "TextBox")).ToList();
        }

        private static string StoryParagraphText(XElement paragraph)
        {
            return string.Concat(paragraph.Descendants()
                .Where(t => t.Name == W + "t" || t.Name == W + "tab" || t.Name == W + "br" || t.Name == W + "cr")
                .Where(t => t.Ancestors(W + "p").FirstOrDefault() == paragraph &&
                    !t.Ancestors(W + "del").Any())
                .Select(t => t.Name == W + "t" ? t.Value : t.Name == W + "tab" ? "\t" : "\n")).TrimEnd();
        }

        private static bool CheckScopedTextRemoved(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            XDocument document = package.Xml("word/document.xml");
            string prefix = RequiredString(task, "targetParagraphStartsWith");
            var candidates = ScopedStoryParagraphs(document, RequiredString(task, "targetParagraphScope"))
                .Where(p => StoryParagraphText(p).StartsWith(prefix, StringComparison.Ordinal)).ToList();
            var accepted = new List<string> { RequiredString(task, "expectedFinalText") };
            if (task.Extra.ContainsKey("acceptedFinalTexts")) accepted.AddRange(RequiredStrings(task, "acceptedFinalTexts"));
            bool match = candidates.Count == 1 && accepted.Contains(StoryParagraphText(candidates[0])) &&
                ScopedStoryParagraphs(document, "AllBodyStories").Sum(p => CountOccurrences(StoryParagraphText(p),
                    RequiredString(task, "removedText"))) == RequiredInt(task, "expectedRemainingCount");
            detail = match ? "The exact target text is removed from its source story; surrounding text is preserved."
                : "The source paragraph is missing, changed, duplicated, or still contains the target text.";
            return match;
        }

        private static bool CheckLocatedReplacement(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var locations = RequiredArray(task, "replacementLocations");
            if (locations.Count == 0) throw new InvalidDataException("Replacement source locations are required.");
            XDocument document = package.Xml("word/document.xml");
            string oldText = RequiredString(task, "oldText"), newText = RequiredString(task, "newText");
            int replaced = 0;
            var selected = new HashSet<XElement>();
            foreach (JObject location in locations)
            {
                string prefix = (string)location["paragraphStartsWith"], scope = (string)location["scope"];
                var expected = location["expectedParagraphs"] as JArray;
                if (string.IsNullOrEmpty(prefix) || expected == null || expected.Count == 0)
                    throw new InvalidDataException("Invalid replacement location metadata.");
                var found = ScopedStoryParagraphs(document, scope)
                    .Where(p => StoryParagraphText(p).StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (found.Count != 1 || !selected.Add(found[0]))
                { detail = "An original replacement location is missing or duplicated."; return false; }
                XElement box = found[0].Ancestors(W + "txbxContent").FirstOrDefault();
                IEnumerable<XElement> paragraphs = box == null ? new[] { found[0] } : box.Elements(W + "p");
                string[] actual = paragraphs.Select(StoryParagraphText).ToArray();
                if (!actual.SequenceEqual(expected.Select(t => (string)t)) || actual.Any(t => t.Contains(oldText)))
                { detail = "An original source occurrence remains or its surrounding source content changed."; return false; }
                replaced += actual.Sum(t => CountOccurrences(t, newText));
            }
            bool match = replaced == RequiredInt(task, "expectedReplacementCount");
            detail = match ? "Every declared original occurrence is replaced; later text in other locations is independent."
                : "The replacement count at the original source locations is incorrect.";
            return match;
        }

        private static bool CheckSpacedTextConvertedToTable(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var children = package.Xml("word/document.xml").Root.Element(W + "body").Elements()
                .Where(e => e.Name != W + "bookmarkStart" && e.Name != W + "bookmarkEnd").ToList();
            var headings = children.Select((e, i) => new { Element = e, Index = i })
                .Where(x => x.Element.Name == W + "p" && StoryParagraphText(x.Element) == RequiredString(task, "sectionHeading")).ToList();
            if (headings.Count != 1) { detail = "The table section heading is missing or duplicated."; return false; }
            int start = headings[0].Index + 1;
            while (start < children.Count && children[start].Name == W + "p" && StoryParagraphText(children[start]).Length == 0) start++;
            if (start >= children.Count || children[start].Name != W + "p" ||
                StoryParagraphText(children[start]) != RequiredString(task, "introParagraph"))
            { detail = "The section introduction is missing or changed."; return false; }
            int end = children.Count;
            if (task.Extra.ContainsKey("followingHeading"))
            {
                var ends = children.Select((e, i) => new { Element = e, Index = i }).Where(x => x.Index > start &&
                    x.Element.Name == W + "p" && StoryParagraphText(x.Element) == RequiredString(task, "followingHeading")).ToList();
                if (ends.Count != 1) { detail = "The following section heading is missing or duplicated."; return false; }
                end = ends[0].Index;
            }
            var between = children.Skip(start + 1).Take(end - start - 1).ToList();
            var tables = between.Where(e => e.Name == W + "tbl").ToList();
            bool match = tables.Count == 1 && !between.Any(e => e.Name == W + "p" &&
                (StoryParagraphText(e).Length > 0 || e.Descendants(W + "tab").Any()));
            if (match)
            {
                XElement table = tables[0];
                match = table.Elements(W + "tr").Count() == RequiredInt(task, "expectedRows") &&
                    (table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() ?? 0) == RequiredInt(task, "expectedColumns") &&
                    TableRowsEqual(table, RequiredStringMatrix(task, "expectedTableRows"));
                if (task.Extra.ContainsKey("expectedTableLayout"))
                    match &= ((string)table.Element(W + "tblPr")?.Element(W + "tblLayout")?.Attribute(W + "type") ?? "autofit")
                        == RequiredString(task, "expectedTableLayout");
            }
            detail = match ? "The complete source block is one table in the declared section with native default layout."
                : "Table location, source content, columns, rows, or default layout is incorrect.";
            return match;
        }

        private static XElement BodyChild(XElement element)
        {
            return element.AncestorsAndSelf().FirstOrDefault(e => e.Parent != null && e.Parent.Name == W + "body");
        }

        private static bool CheckThemeTextBoxText(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            XDocument document = package.Xml("word/document.xml");
            var children = document.Root.Element(W + "body").Elements().ToList();
            var paragraphs = ScopedStoryParagraphs(document, "AllBodyStories");
            var starts = paragraphs.Where(p => StoryParagraphText(p) == RequiredString(task, "precedingSectionText")).ToList();
            var ends = paragraphs.Where(p => StoryParagraphText(p) == RequiredString(task, "followingSectionText")).ToList();
            if (starts.Count != 1 || ends.Count != 1)
            { detail = "The textbox section boundaries are missing or duplicated."; return false; }
            int first = children.IndexOf(BodyChild(starts[0])), last = children.IndexOf(BodyChild(ends[0]));
            var candidates = TextBoxCandidates(document).Where(c => c.Geometry == RequiredString(task, "shapeGeometry") &&
                children.IndexOf(BodyChild(c.OuterParagraph)) > first && children.IndexOf(BodyChild(c.OuterParagraph)) < last)
                .Where(c => c.TextBox.Parent.Name == Wps + "txbx").ToList();
            var matches = new List<TextBoxCandidate>();
            foreach (var candidate in candidates)
            {
                XElement shape = candidate.TextBox.Parent.Parent, properties = shape.Element(Wps + "spPr");
                XElement scheme = properties?.Element(A + "solidFill")?.Element(A + "schemeClr");
                if ((string)scheme?.Attribute("val") != RequiredString(task, "expectedFillScheme") ||
                    scheme.Elements().Count() != 1 ||
                    DrawingInteger(scheme.Element(A + "lumMod"), "val", -1) != RequiredInt(task, "expectedFillLumMod")) continue;
                XElement theme = package.Xml("word/theme/theme1.xml").Descendants(A + "clrScheme").Single()
                    .Element(A + RequiredString(task, "expectedFillThemeSlot"))?.Elements().FirstOrDefault();
                if (!string.Equals((string)theme?.Attribute("val") ?? (string)theme?.Attribute("lastClr"),
                    RequiredString(task, "expectedFillThemeRgb"), StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(candidate);
            }
            if (matches.Count != 1) { detail = "The original dark-blue rectangle is missing, changed or duplicated."; return false; }
            XElement boxText = matches[0].TextBox, shapeProperties = boxText.Parent.Parent.Element(Wps + "spPr");
            XElement extent = shapeProperties.Element(A + "xfrm")?.Element(A + "ext");
            bool match = string.Join("\n", boxText.Elements(W + "p").Select(StoryParagraphText)) == RequiredString(task, "expectedText") &&
                DrawingInteger(extent, "cx", -1) == RequiredInt(task, "expectedWidthEmu") &&
                DrawingInteger(extent, "cy", -1) == RequiredInt(task, "expectedHeightEmu") &&
                DrawingInteger(shapeProperties.Element(A + "xfrm"), "rot", 0) == RequiredInt(task, "expectedRotation") &&
                new[] { "1", "true" }.Contains((string)shapeProperties.Element(A + "xfrm")?.Attribute("flipH")) == OptionalBool(task, "expectedFlipH", false) &&
                new[] { "1", "true" }.Contains((string)shapeProperties.Element(A + "xfrm")?.Attribute("flipV")) == OptionalBool(task, "expectedFlipV", false);
            var anchor = boxText.Ancestors(Wp + "anchor").FirstOrDefault();
            if (task.Extra.ContainsKey("expectedPositionH")) match &=
                PositionMatches(anchor?.Element(Wp + "positionH"), task.Extra["expectedPositionH"] as JObject) &&
                PositionMatches(anchor?.Element(Wp + "positionV"), task.Extra["expectedPositionV"] as JObject);
            foreach (XElement p in boxText.Elements(W + "p"))
            {
                match &= (string)p.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val") == RequiredString(task, "expectedAlignment");
                foreach (XElement run in p.Descendants(W + "r").Where(r => r.Descendants(W + "t").Any(t => t.Value.Length > 0)))
                {
                    var actual = EffectiveRunFormatting(package, p, run);
                    if (OptionalBool(task, "requireExactRunFormatting", false))
                        match &= actual.Count == ((JObject)task.Extra["expectedRunFormatting"]).Count;
                    foreach (var property in (JObject)task.Extra["expectedRunFormatting"])
                        match &= actual.ContainsKey(property.Key) && actual[property.Key] == (string)property.Value;
                }
            }
            detail = match ? "The correct section textbox contains the exact text with its original appearance and placement."
                : "Textbox text, size, position, alignment or font formatting is changed.";
            return match;
        }

        private static bool PositionMatches(XElement position, JObject expected)
        {
            if (position == null || expected == null) return false;
            return (string)position.Attribute("relativeFrom") == (string)expected["relativeFrom"] &&
                (string)position.Element(Wp + "posOffset") == (string)expected["posOffset"] &&
                (string)position.Element(Wp + "align") == (string)expected["align"];
        }
    }
}
