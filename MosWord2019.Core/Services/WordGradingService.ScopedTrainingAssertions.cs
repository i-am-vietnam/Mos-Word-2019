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
                if (location["sectionStartText"] != null)
                {
                    var sectionTask = new TaskDefinition { Extra = location.Properties().ToDictionary(p => p.Name, p => p.Value) };
                    var section = DeclaredBodySection(document, sectionTask);
                    found = section == null ? new List<XElement>() : found.Where(p => section.Contains(BodyChild(p))).ToList();
                }
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
            if (OptionalBool(task, "requireNoOriginalText", false))
                match &= !ScopedStoryParagraphs(document, "AllBodyStories").Any(p => StoryParagraphText(p).Contains(oldText));
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

        private static List<XElement> DeclaredBodySection(XDocument document, TaskDefinition task)
        {
            var children = document.Root.Element(W + "body").Elements().ToList();
            Func<string, List<XElement>> boundary = key => ScopedStoryParagraphs(document, "MainBody")
                .Where(p => MatchesDeclaredText(task, key, StoryParagraphText(p))).Select(BodyChild).Distinct().ToList();
            var starts = boundary("sectionStartText");
            var ends = OptionalBool(task, "sectionToDocumentEnd", false) ? new List<XElement>() : boundary("sectionEndText");
            if (starts.Count != 1 || (!OptionalBool(task, "sectionToDocumentEnd", false) && ends.Count != 1)) return null;
            Func<XElement, string, bool> kind = (element, key) =>
            {
                if (!task.Extra.ContainsKey(key)) return true;
                string value = RequiredString(task, key);
                if (value != "paragraph" && value != "singleCellTable") throw new InvalidDataException("Unsupported semantic section boundary kind.");
                return value == "paragraph" ? element.Name == W + "p" : element.Name == W + "tbl" &&
                    element.Elements(W + "tr").Count() == 1 && element.Descendants(W + "tc").Count() == 1;
            };
            if (!kind(starts[0], "sectionStartKind") || (ends.Count > 0 && !kind(ends[0], "sectionEndKind"))) return null;
            int first = children.IndexOf(starts[0]), last = ends.Count == 0 ? children.Count : children.IndexOf(ends[0]);
            return first < last ? children.Skip(first + 1).Take(last - first - 1).ToList() : null;
        }

        private static bool SectionParagraphsPreserved(List<XElement> section, TaskDefinition task)
        {
            if (!task.Extra.ContainsKey("preservedSectionParagraphs")) return true;
            var declared = RequiredArray(task, "preservedSectionParagraphs");
            if (declared.Any(t => t.Type != JTokenType.String && (!(t is JArray) || ((JArray)t).Count == 0 || ((JArray)t).Any(v => v.Type != JTokenType.String))))
                throw new InvalidDataException("Invalid preserved section paragraph metadata.");
            var actual = section.Where(p => p.Name == W + "p").Select(StoryParagraphText).Where(t => t.Length > 0).ToList();
            return actual.Count == declared.Count && actual.Select((text, i) => declared[i].Type == JTokenType.String
                ? text == (string)declared[i] : ((JArray)declared[i]).Values<string>().Contains(text)).All(v => v);
        }

        private static bool CheckSourceBlockConvertedToTable(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var section = DeclaredBodySection(package.Xml("word/document.xml"), task);
            detail = "The declared source block, heading, surrounding prose or converted table differs.";
            if (section == null) return false;
            var intros = section.Where(e => e.Name == W + "p" && StoryParagraphText(e) == RequiredString(task, "introParagraph")).ToList();
            var ends = section.Where(e => e.Name == W + "p" && MatchesDeclaredText(task, "followingParagraph", StoryParagraphText(e))).ToList();
            if (intros.Count != 1 || ends.Count != 1 || section.IndexOf(intros[0]) >= section.IndexOf(ends[0])) return false;
            var block = section.Skip(section.IndexOf(intros[0]) + 1).Take(section.IndexOf(ends[0]) - section.IndexOf(intros[0]) - 1).ToList();
            var tables = block.Where(e => e.Name == W + "tbl").ToList();
            bool match = tables.Count == 1 && block.Where(e => e.Name != W + "tbl").All(e =>
                e.Name == W + "bookmarkStart" || e.Name == W + "bookmarkEnd" ||
                (e.Name == W + "p" && StoryParagraphText(e).Length == 0 && !e.Descendants().Any(n =>
                    n.Name == W + "drawing" || n.Name == W + "pict" || n.Name == W + "object" || n.Name == W + "fldChar" || n.Name == W + "instrText")));
            if (match)
            {
                var table = tables[0];
                match = TableRowsEqual(table, RequiredStringMatrix(task, "expectedTableRows")) &&
                    table.Elements(W + "tr").Count() == RequiredInt(task, "expectedRows") &&
                    table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() == RequiredInt(task, "expectedColumns") &&
                    !table.Descendants().Any(e => e.Name == W + "gridSpan" || e.Name == W + "vMerge");
                if (task.Extra.ContainsKey("expectedTableLayout"))
                {
                    XElement layout = table.Element(W + "tblPr")?.Element(W + "tblLayout");
                    // Word's Convert Text to Table dialog can omit tblLayout even with
                    // its default "Fixed column width: Auto" selected. API conversion
                    // writes fixed explicitly; only declared packages accept omission.
                    match &= (layout == null && OptionalBool(task, "allowOmittedTableLayout", false)) ||
                        ((string)layout?.Attribute(W + "type") ?? "autofit") == RequiredString(task, "expectedTableLayout");
                }
            }
            var source = RequiredStrings(task, "sourceParagraphs");
            match &= !MainParagraphs(package).Any(p => source.Contains(StoryParagraphText(p)));
            match &= SectionParagraphsPreserved(section, task);
            if (match) detail = "Only the declared tab-delimited source block is converted, preserving its heading and neighboring prose.";
            return match;
        }

        private static bool CheckSectionRgbTextBox(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var document = package.Xml("word/document.xml");
            var section = DeclaredBodySection(document, task);
            var all = TextBoxCandidates(document).ToList();
            var boxes = section == null ? new List<TextBoxCandidate>() : all.Where(c => section.Contains(BodyChild(c.OuterParagraph))).ToList();
            detail = "The original section-scoped textbox, exact text, fill, size or relative placement differs.";
            if (all.Count != RequiredInt(task, "expectedTextBoxCount") || boxes.Count != 1) return false;
            var box = boxes[0];
            if (box.TextBox.Parent.Name != Wps + "txbx") return false;
            var shape = box.TextBox.Parent.Parent;
            var properties = shape.Element(Wps + "spPr");
            var anchor = shape.Ancestors(Wp + "anchor").FirstOrDefault();
            var extent = properties?.Element(A + "xfrm")?.Element(A + "ext");
            var widths = RequiredArray(task, "expectedWidthsEmu").Select(t => (long)t).ToArray();
            bool match = box.Geometry == RequiredString(task, "shapeGeometry") &&
                NormalizeColor(box.FillColor) == NormalizeColor(RequiredString(task, "fillColor")) &&
                string.Join("\n", box.TextBox.Elements(W + "p").Select(StoryParagraphText)) == RequiredString(task, "expectedText") &&
                widths.Contains(DrawingInteger(extent, "cx", -1)) &&
                widths.Contains(DrawingInteger(anchor?.Element(Wp + "extent"), "cx", -1)) &&
                DrawingInteger(extent, "cy", -1) == RequiredInt(task, "expectedHeightEmu") &&
                DrawingInteger(anchor?.Element(Wp + "extent"), "cy", -1) == RequiredInt(task, "expectedHeightEmu") &&
                PositionMatches(anchor?.Element(Wp + "positionH"), task.Extra["expectedPositionH"] as JObject) &&
                PositionMatches(anchor?.Element(Wp + "positionV"), task.Extra["expectedPositionV"] as JObject);
            match &= SectionParagraphsPreserved(section, task);
            XNamespace wp14 = "http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing";
            if (task.Extra.ContainsKey("expectedRotation"))
                match &= DrawingInteger(properties?.Element(A + "xfrm"), "rot", 0) == RequiredInt(task, "expectedRotation") &&
                    new[] { "1", "true" }.Contains((string)properties?.Element(A + "xfrm")?.Attribute("flipH")) == OptionalBool(task, "expectedFlipH", false) &&
                    new[] { "1", "true" }.Contains((string)properties?.Element(A + "xfrm")?.Attribute("flipV")) == OptionalBool(task, "expectedFlipV", false);
            if (task.Extra.ContainsKey("expectedRelativeWidth"))
                match &= (string)anchor?.Element(wp14 + "sizeRelH")?.Attribute("relativeFrom") == "margin" &&
                    (string)anchor?.Element(wp14 + "sizeRelH")?.Element(wp14 + "pctWidth") == RequiredString(task, "expectedRelativeWidth");
            if (match) detail = "The original logical rectangle contains the exact text with its native size/fill and relative placement preserved.";
            return match;
        }

        private static bool CheckSectionCommentDeleted(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var section = DeclaredBodySection(package.Xml("word/document.xml"), task);
            var paragraphs = section == null ? new List<XElement>() : section.Where(e => e.Name == W + "p" &&
                MatchesDeclaredText(task, "expectedParagraph", StoryParagraphText(e))).ToList();
            detail = "The original comment or its anchor remains, or its source narrative/section changed.";
            if (paragraphs.Count != 1 || HasActiveCommentOnText(paragraphs[0], RequiredString(task, "targetText"))) return false;
            XDocument comments;
            if (package.TryXml("word/comments.xml", out comments) && comments.Root.Elements(W + "comment").Any(c =>
                VisibleText(c).TrimEnd() == RequiredString(task, "deletedCommentText") &&
                (!task.Extra.ContainsKey("deletedCommentAuthor") || (string)c.Attribute(W + "author") == RequiredString(task, "deletedCommentAuthor")))) return false;
            if (OptionalBool(task, "requireConsistentCommentMetadata", false))
            {
                var liveIds = new HashSet<string>(comments == null ? Enumerable.Empty<string>() :
                    comments.Root.Elements(W + "comment").Select(c => (string)c.Attribute(W + "id")), StringComparer.Ordinal);
                if (paragraphs[0].Descendants().Where(e => e.Name == W + "commentRangeStart" || e.Name == W + "commentRangeEnd" || e.Name == W + "commentReference")
                    .Any(e => !liveIds.Contains((string)e.Attribute(W + "id")))) return false;
                XNamespace w14 = "http://schemas.microsoft.com/office/word/2010/wordml";
                XNamespace w15 = "http://schemas.microsoft.com/office/word/2012/wordml";
                var liveParagraphIds = new HashSet<string>(comments == null ? Enumerable.Empty<string>() :
                    comments.Descendants(W + "p").Select(p => (string)p.Attribute(w14 + "paraId")), StringComparer.Ordinal);
                XDocument extended;
                if (package.TryXml("word/commentsExtended.xml", out extended) && extended.Descendants(w15 + "commentEx")
                    .Any(e => !liveParagraphIds.Contains((string)e.Attribute(w15 + "paraId")))) return false;
            }
            detail = "The comment in the declared section is deleted while its original text and other stories remain intact.";
            return true;
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
