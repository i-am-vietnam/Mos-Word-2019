using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static readonly XNamespace Wpg = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";

        public static bool RequiresTocLayout(TaskDefinition task)
        { return task?.AssertionType == "TableOfContentsLevels" && OptionalBool(task, "requireRefreshedEntries", false); }

        // Retain real text tabs/breaks; tab-stop declarations in pPr are not characters.
        private static string LogicalParagraphText(XElement paragraph)
        {
            return string.Concat(paragraph.Descendants().Where(e =>
                (e.Name == W + "t" || e.Name == W + "tab" || e.Name == W + "br" || e.Name == W + "cr") &&
                e.Ancestors(W + "r").Any() && e.Ancestors(W + "p").FirstOrDefault() == paragraph &&
                !e.Ancestors(W + "del").Any()).Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : "\n"));
        }

        private static bool CheckBodyContentBeforeHeading(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var document = package.Xml("word/document.xml");
            var body = document.Root.Element(W + "body");
            string[] expected = RequiredStrings(task, "expectedParagraphs");
            var paragraphs = body.Elements(W + "p").ToList();
            var targets = paragraphs.Where(p => LogicalParagraphText(p) == RequiredString(task, "followingHeading")).ToList();
            int index = targets.Count == 1 ? paragraphs.IndexOf(targets[0]) : -1;
            bool match = index >= expected.Length && expected.Length > 0 &&
                paragraphs.Skip(index - expected.Length).Take(expected.Length).Select(LogicalParagraphText).SequenceEqual(expected) &&
                expected.All(text => document.Descendants(W + "p").Count(p => LogicalParagraphText(p) == text) == 1);
            // The sequence must be immediately adjacent as body nodes, not on opposite sides of a table/object.
            if (match)
            {
                var nodes = body.Elements().ToList();
                int end = nodes.IndexOf(targets[0]);
                match = end >= expected.Length && nodes.Skip(end - expected.Length).Take(expected.Length)
                    .SequenceEqual(paragraphs.Skip(index - expected.Length).Take(expected.Length));
            }
            detail = "The complete exact paragraph sequence must occur once immediately before the original body heading.";
            return match;
        }

        private static bool CheckDocumentBackground(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var backgrounds = package.Xml("word/document.xml").Root.Elements(W + "background").ToList();
            var background = backgrounds.Count == 1 ? backgrounds[0] : null;
            string theme = (string)background?.Attribute(W + "themeColor");
            string expectedTheme = RequiredString(task, "expectedThemeColor");
            var color = package.Xml("word/theme/theme1.xml").Descendants(A + "clrScheme").Single().Element(A + expectedTheme)?.Elements().FirstOrDefault();
            string themeRgb = (string)color?.Attribute("val") ?? (string)color?.Attribute("lastClr");
            bool match = background != null && !background.Elements().Any() &&
                string.Equals((string)background.Attribute(W + "color"), RequiredString(task, "expectedRgb"), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(themeRgb, RequiredString(task, "expectedThemeRgb"), StringComparison.OrdinalIgnoreCase) &&
                background.Attribute(W + "themeShade") == null &&
                (theme == expectedTheme && string.Equals((string)background.Attribute(W + "themeTint"), RequiredString(task, "expectedTint"), StringComparison.OrdinalIgnoreCase) ||
                 theme == null && OptionalBool(task, "allowOmittedThemeColor", false) && background.Attribute(W + "themeTint") == null);
            detail = "The real document background must have the verified native theme/tint and effective RGB color.";
            return match;
        }

        private static bool CheckTextBoxAssembly(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            string expected = RequiredString(task, "expectedText");
            var groups = body.Descendants(Wpg + "wgp").Where(g => !g.Ancestors(Mc + "Fallback").Any() &&
                g.Descendants(W + "p").Any(p => LogicalParagraphText(p) == expected)).ToList();
            bool match = groups.Count == 1;
            if (match)
            {
                var group = groups[0]; var anchor = group.Ancestors(Wp + "anchor").FirstOrDefault();
                var shapes = group.Elements(Wps + "wsp").ToList();
                var sdt = anchor?.Ancestors(W + "sdt").FirstOrDefault(s =>
                    (string)s.Element(W + "sdtPr")?.Element(W + "docPartObj")?.Element(W + "docPartGallery")?.Attribute(W + "val") == RequiredString(task, "anchorGallery"));
                var cover = body.Descendants(W + "sdt").Where(s => !s.Ancestors(W + "txbxContent").Any() && (string)s.Element(W + "sdtPr")?.Element(W + "docPartObj")?
                    .Element(W + "docPartGallery")?.Attribute(W + "val") == RequiredString(task, "anchorGallery")).ToList();
                var anchorParagraph = anchor?.Ancestors(W + "p").FirstOrDefault();
                bool coverAnchor = cover.Count == 1 && anchorParagraph != null && (sdt == cover[0] || BodyChild(anchorParagraph) == BodyChild(cover[0]) ||
                    anchorParagraph.Parent == body && XNode.CompareDocumentOrder(anchorParagraph, cover[0]) < 0 &&
                    body.Elements().TakeWhile(e => e != cover[0]).All(e => e.Name == W + "p" && string.IsNullOrWhiteSpace(LogicalParagraphText(e)) &&
                        !e.Descendants(W + "sectPr").Any() && !e.Descendants(W + "br").Any(b => (string)b.Attribute(W + "type") == "page")));
                if (coverAnchor && sdt != null)
                    coverAnchor = !cover[0].Descendants(W + "br").Where(b => !b.Ancestors(W + "txbxContent").Any() &&
                        (string)b.Attribute(W + "type") == "page").Any(b => XNode.CompareDocumentOrder(b, anchorParagraph) < 0);
                match = anchor != null && !anchor.Ancestors(W + "txbxContent").Any() && coverAnchor &&
                    shapes.Count == 2 && group.Elements(Wpg + "wgp").Count() == 0 &&
                    anchor.Element(Wp + "wrapSquare") != null && (string)anchor.Attribute("behindDoc") != "1" &&
                    (string)anchor.Element(Wp + "positionH")?.Attribute("relativeFrom") == RequiredString(task, "expectedHorizontalRelativeFrom") &&
                    (string)anchor.Element(Wp + "positionH")?.Element(Wp + "align") == RequiredString(task, "expectedHorizontalAlignment") &&
                    (string)anchor.Element(Wp + "positionV")?.Attribute("relativeFrom") == RequiredString(task, "expectedVerticalRelativeFrom") &&
                    (string)anchor.Element(Wp + "positionV")?.Element(Wp + "align") == RequiredString(task, "expectedVerticalAlignment") &&
                    NearDrawingValue(anchor.Element(Wp + "extent"), "cx", RequiredInt(task, "expectedWidthEmu"), 2540);
                if (match)
                {
                    var band = shapes[0]; var text = shapes[1];
                    var bandPr = band.Element(Wps + "spPr"); var textPr = text.Element(Wps + "spPr");
                    var bandTransform = bandPr?.Element(A + "xfrm"); var textTransform = textPr?.Element(A + "xfrm");
                    var frame = group.Element(Wpg + "grpSpPr")?.Element(A + "xfrm");
                    int width = RequiredInt(task, "expectedWidthEmu");
                    match = bandTransform != null && textTransform != null && frame != null && UntransformedDrawing(frame) &&
                        NearDrawingValue(frame.Element(A + "off"), "x", 0, 2540) && NearDrawingValue(frame.Element(A + "off"), "y", 0, 2540) &&
                        NearDrawingValue(frame.Element(A + "chOff"), "x", 0, 2540) && NearDrawingValue(frame.Element(A + "chOff"), "y", 0, 2540) &&
                        (string)bandPr.Element(A + "prstGeom")?.Attribute("prst") == "rect" &&
                        (string)textPr.Element(A + "prstGeom")?.Attribute("prst") == "rect" &&
                        (string)bandPr.Element(A + "solidFill")?.Element(A + "schemeClr")?.Attribute("val") == RequiredString(task, "expectedBandThemeColor") &&
                        !bandPr.Element(A + "solidFill").Elements().Single().Elements().Any() && textPr.Element(A + "noFill") != null &&
                        bandPr.Element(A + "ln")?.Element(A + "noFill") != null && textPr.Element(A + "ln")?.Element(A + "noFill") != null &&
                        UntransformedDrawing(bandTransform) && UntransformedDrawing(textTransform) &&
                        NearDrawingValue(bandTransform.Element(A + "off"), "x", 0, 2540) && NearDrawingValue(bandTransform.Element(A + "off"), "y", 0, 2540) &&
                        NearDrawingValue(bandTransform.Element(A + "ext"), "cx", width, 2540) &&
                        NearDrawingValue(bandTransform.Element(A + "ext"), "cy", RequiredInt(task, "expectedBandHeightEmu"), 2540) &&
                        NearDrawingValue(textTransform.Element(A + "off"), "x", 0, 2540) &&
                        NearDrawingValue(textTransform.Element(A + "off"), "y", 252692, 2540) &&
                        NearDrawingValue(textTransform.Element(A + "ext"), "cx", width, 2540) &&
                        NearDrawingValue(frame.Element(A + "ext"), "cx", width, 2540) &&
                        NearDrawingValue(frame.Element(A + "chExt"), "cx", width, 2540) &&
                        NearDrawingValue(frame.Element(A + "ext"), "cy", DrawingInteger(anchor.Element(Wp + "extent"), "cy", -10000), 2540) &&
                        NearDrawingValue(frame.Element(A + "ext"), "cy", DrawingInteger(frame.Element(A + "chExt"), "cy", -10000), 2540) &&
                        NearDrawingValue(frame.Element(A + "chExt"), "cy", DrawingInteger(textTransform.Element(A + "off"), "y", -10000) +
                            DrawingInteger(textTransform.Element(A + "ext"), "cy", -10000), 2540) &&
                        band.Descendants(W + "p").All(p => string.IsNullOrWhiteSpace(LogicalParagraphText(p))) &&
                        text.Descendants(W + "p").Select(LogicalParagraphText).Where(t => t.Length > 0).SequenceEqual(new[] { expected }) &&
                        (string)band.Element(Wps + "bodyPr")?.Attribute("anchor") == "ctr" &&
                        text.Element(Wps + "bodyPr")?.Element(A + "spAutoFit") != null;
                }
            }
            detail = "The native grouped band/text assembly must retain its cover-body anchor, exact text, geometry and whole-group wrapping/position.";
            return match;
        }

        private static bool UntransformedDrawing(XElement transform)
        {
            string rotation = (string)transform.Attribute("rot");
            return (rotation == null || rotation == "0") && new[] { "flipH", "flipV" }.All(name =>
            {
                string value = (string)transform.Attribute(name);
                return value == null || value == "0" || value == "false" || value == "off";
            });
        }

        private static bool NearDrawingValue(XElement element, string attribute, int expected, int tolerance)
        { return element != null && Math.Abs((long)DrawingInteger(element, attribute, int.MinValue) - expected) <= tolerance; }

        private static bool ActiveHeaderFooterStoriesEmpty(PackageSnapshot package)
        {
            var document = package.Xml("word/document.xml");
            bool even = IsOn(package.Xml("word/settings.xml").Root.Element(W + "evenAndOddHeaders"));
            var inherited = new Dictionary<string, string>();
            foreach (var section in DocumentSections(document))
            {
                var references = section.Properties.Elements().Where(e => e.Name == W + "headerReference" || e.Name == W + "footerReference").ToList();
                if (references.GroupBy(e => e.Name.LocalName + ":" + (string)e.Attribute(W + "type")).Any(g => g.Count() != 1)) return false;
                foreach (var reference in references)
                    inherited[reference.Name.LocalName + ":" + (string)reference.Attribute(W + "type")] = (string)reference.Attribute(R + "id");
                var types = new List<string> { "default" };
                if (even) types.Add("even");
                if (IsOn(section.Properties.Element(W + "titlePg"))) types.Add("first");
                foreach (string kind in new[] { "headerReference", "footerReference" })
                    foreach (string type in types)
                    {
                        string id; if (!inherited.TryGetValue(kind + ":" + type, out id)) continue;
                        string part = package.RelatedPart("word/document.xml", id); XDocument story;
                        if (part == null || !package.TryXml(part, out story) || HeaderHasVisibleContent(story.Root)) return false;
                    }
            }
            return true;
        }

        private static bool CheckDeclaredDocumentPreservation(PackageSnapshot package, TaskDefinition task)
        {
            if (task.Extra.ContainsKey("preservedOutlineLevels"))
                foreach (JObject expected in RequiredArray(task, "preservedOutlineLevels"))
                {
                    var paragraphs = package.Xml("word/document.xml").Root.Element(W + "body").Elements(W + "p")
                        .Where(p => LogicalParagraphText(p) == (string)expected["text"]).ToList();
                    // Imported paragraphs are an optional authorized body variant.
                    if (paragraphs.Any(p => EffectiveOutlineLevel(package, p) != (int)expected["level"])) return false;
                }
            if (task.Extra.ContainsKey("preservedCoreProperties"))
                foreach (JObject property in RequiredArray(task, "preservedCoreProperties"))
                    if ((string)package.Xml("docProps/core.xml").Root.Element(XName.Get((string)property["name"])) != (string)property["value"]) return false;
            if (task.Extra.ContainsKey("preservedThemeColors"))
            {
                var colors = package.Xml("word/theme/theme1.xml").Descendants(A + "clrScheme").Single();
                foreach (var property in ((JObject)task.Extra["preservedThemeColors"]).Properties())
                {
                    var color = colors.Element(A + property.Name)?.Elements().FirstOrDefault();
                    if (((string)color?.Attribute("lastClr") ?? (string)color?.Attribute("val")) != (string)property.Value) return false;
                }
            }
            if (task.Extra.ContainsKey("preservedCoverGallery"))
            {
                var covers = package.Xml("word/document.xml").Root.Element(W + "body").Descendants(W + "sdt").Where(s => !s.Ancestors(W + "txbxContent").Any() &&
                    (string)s.Element(W + "sdtPr")?.Element(W + "docPartObj")?.Element(W + "docPartGallery")?.Attribute(W + "val") == RequiredString(task, "preservedCoverGallery")).ToList();
                if (covers.Count != 1 || !covers[0].Descendants(W + "tbl").Any() || !covers[0].Descendants(W + "drawing").Any()) return false;
            }
            if (task.Extra.ContainsKey("preservedToc"))
            {
                var expected = task.Extra["preservedToc"] as JObject;
                if (expected == null) throw new InvalidDataException("Invalid preserved TOC metadata.");
                var body = package.Xml("word/document.xml").Root.Element(W + "body");
                var fields = BodyFields(body).Where(f => Regex.IsMatch(f.Code.ToString(), @"^\s*TOC\b", RegexOptions.IgnoreCase)).ToList();
                if (fields.Count != 1 || NormalizeFieldCode(fields[0].Code.ToString()) != NormalizeFieldCode((string)expected["fieldCode"])) return false;
                var toc = body.Elements(W + "sdt").Where(s => s.Descendants(W + "p").Any(fields[0].ResultParagraphs.Contains)).ToList();
                var before = body.Elements(W + "p").Where(p => LogicalParagraphText(p).TrimEnd() == (string)expected["precedingParagraph"]).ToList();
                var after = body.Elements(W + "p").Where(p => LogicalParagraphText(p) == (string)expected["followingHeading"]).ToList();
                if (toc.Count != 1 || before.Count != 1 || after.Count != 1 ||
                    XNode.CompareDocumentOrder(before[0], toc[0]) >= 0 || XNode.CompareDocumentOrder(toc[0], after[0]) >= 0) return false;
            }
            return true;
        }

        private static bool CheckRefreshedToc(PackageSnapshot package, TaskDefinition task, WordTocLayoutSnapshot layout, out string detail)
        {
            detail = "The existing live TOC must contain every current heading in order, valid links and pages matching the owned document's current layout.";
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            var fields = BodyFields(body).Where(f => Regex.IsMatch(f.Code.ToString(), @"^\s*TOC\b", RegexOptions.IgnoreCase)).ToList();
            if (fields.Count != 1 || NormalizeFieldCode(fields[0].Code.ToString()) != NormalizeFieldCode(RequiredString(task, "expectedFieldCode"))) return false;
            var generated = GeneratedTocParagraphs(body);
            string[] scope = RequiredString(task, "headingRange").Split('-');
            int lower, upper;
            if (scope.Length != 2 || !int.TryParse(scope[0], out lower) || !int.TryParse(scope[1], out upper) || lower < 1 || upper > 9 || lower > upper)
                throw new InvalidDataException("Invalid TOC heading scope.");
            var headings = body.Descendants(W + "p").Where(p => !p.Ancestors(W + "txbxContent").Any() && !generated.Contains(p) &&
                !string.IsNullOrWhiteSpace(LogicalParagraphText(p)) &&
                EffectiveOutlineLevel(package, p) >= lower - 1 && EffectiveOutlineLevel(package, p) <= upper - 1).ToList();
            var rows = body.Descendants(W + "p").Where(fields[0].ResultParagraphs.Contains).ToList();
            if (headings.Count == 0 || rows.Count != headings.Count) return false;
            var sdts = body.Elements(W + "sdt").Where(s => rows.All(p => p.Ancestors(W + "sdt").Contains(s)) &&
                (string)s.Element(W + "sdtPr")?.Element(W + "docPartObj")?.Element(W + "docPartGallery")?.Attribute(W + "val") == "Table of Contents").ToList();
            if (sdts.Count != 1) return false;
            var children = body.Elements().ToList(); int position = children.IndexOf(sdts[0]);
            var before = body.Elements(W + "p").Where(p => LogicalParagraphText(p).TrimEnd() == RequiredString(task, "precedingParagraph")).ToList();
            var after = headings.Where(p => LogicalParagraphText(p) == RequiredString(task, "followingHeading")).ToList();
            if (before.Count != 1 || after.Count != 1 || children.IndexOf(before[0]) >= position || children.IndexOf(BodyChild(after[0])) <= position) return false;
            var used = new HashSet<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; var heading = headings[i];
                var links = row.Elements(W + "hyperlink").ToList();
                if (links.Count != 1 || links[0].Attribute(R + "id") != null) return false;
                string bookmark = (string)links[0].Attribute(W + "anchor");
                if (string.IsNullOrEmpty(bookmark) || !used.Add(bookmark)) return false;
                var starts = body.Descendants(W + "bookmarkStart").Where(b => (string)b.Attribute(W + "name") == bookmark).ToList();
                if (starts.Count != 1 || starts[0].Ancestors(W + "p").FirstOrDefault() != heading) return false;
                string id = (string)starts[0].Attribute(W + "id");
                var ends = body.Descendants(W + "bookmarkEnd").Where(b => (string)b.Attribute(W + "id") == id).ToList();
                if (ends.Count != 1 || XNode.CompareDocumentOrder(starts[0], ends[0]) >= 0) return false;
                var rangeText = body.Descendants().SkipWhile(e => e != starts[0]).Skip(1).TakeWhile(e => e != ends[0])
                    .Where(e => e.Name == W + "t").Select(e => e.Value);
                string title = LogicalParagraphText(heading);
                if (string.Concat(rangeText) != title) return false;
                var pageFields = BodyFields(row).Where(f => Regex.IsMatch(f.Code.ToString(), @"^\s*PAGEREF\b", RegexOptions.IgnoreCase)).ToList();
                if (pageFields.Count != 1 || NormalizeFieldCode(pageFields[0].Code.ToString()) != "PAGEREF " + bookmark + " \\h") return false;
                string page = pageFields[0].Result.ToString();
                if (LogicalParagraphText(row) != title + "\t" + page) return false;
                if ((string)row.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") != "TOC" + (EffectiveOutlineLevel(package, heading) + 1)) return false;
                var targets = layout.Targets.Where(t => t != null && t.Bookmark == bookmark).ToList();
                if (targets.Count != 1 || targets[0].Text != title || targets[0].AdjustedPageNumber < 1)
                    throw new InvalidDataException("The current layout observation for a valid TOC target is unavailable or does not match the saved heading.");
                var section = DocumentSections(body.Document).Single(s => s.Content.Any(e => e == BodyChild(heading)));
                string format = (string)section.Properties.Element(W + "pgNumType")?.Attribute(W + "fmt") ?? "decimal";
                if (page != PageLabel(targets[0].AdjustedPageNumber, format)) return false;
            }
            return true;
        }

        private static int EffectiveOutlineLevel(PackageSnapshot package, XElement paragraph)
        {
            var styles = package.Xml("word/styles.xml").Root;
            var sources = StyleChain(styles, (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "Normal")
                .Select(s => s.Element(W + "pPr")).Concat(new[] { paragraph.Element(W + "pPr") });
            return sources.Where(p => p?.Element(W + "outlineLvl") != null).Select(p => Twips(p.Element(W + "outlineLvl"), "val", 9)).DefaultIfEmpty(9).Last();
        }

        private static string PageLabel(int number, string format)
        {
            if (format == "decimal") return number.ToString(CultureInfo.InvariantCulture);
            string result = "";
            if (format == "upperRoman" || format == "lowerRoman")
            {
                int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
                string[] symbols = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
                if (number > 3999) throw new InvalidDataException("Unsupported Roman page label.");
                for (int i = 0; i < values.Length; i++) while (number >= values[i]) { result += symbols[i]; number -= values[i]; }
                return format == "lowerRoman" ? result.ToLowerInvariant() : result;
            }
            if (format == "upperLetter" || format == "lowerLetter")
            {
                while (number > 0) { number--; result = (char)('A' + number % 26) + result; number /= 26; }
                return format == "lowerLetter" ? result.ToLowerInvariant() : result;
            }
            throw new InvalidDataException("The observed section page-number format is not supported for TOC layout comparison.");
        }
    }
}
