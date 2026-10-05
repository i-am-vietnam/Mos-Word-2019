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
        private static string[] RequiredHeader(TaskDefinition task)
        {
            var header = RequiredArray(task, "targetHeaderRow");
            if (header.Any(t => t.Type != JTokenType.String)) throw new InvalidDataException("Invalid table header metadata.");
            return header.Values<string>().ToArray();
        }
        // Scope is optional so established packages retain their original target contracts.
        private static List<XElement> FindTaskTables(XDocument document, string[] header, TaskDefinition task)
        {
            var tables = FindTablesByHeader(document, header);
            JToken heading;
            return task.Extra.TryGetValue("precedingHeading", out heading)
                ? tables.Where(t => PreviousNonemptyParagraph(t) == (string)heading).ToList() : tables;
        }

        private static List<SmartArtReference> FindTaskSmartArts(PackageSnapshot package, TaskDefinition task)
        {
            JToken heading;
            var targets = task.Extra.TryGetValue("precedingHeading", out heading)
                ? AllSmartArts(package).Where(s => PreviousNonemptyParagraph(s.Container.Ancestors(W + "p").FirstOrDefault()) == (string)heading).ToList()
                : FindSmartArts(package, RequiredString(task, "anchorText"));
            JToken text;
            if (task.Extra.TryGetValue("expectedTextItems", out text))
                targets = targets.Where(s => package.Xml(s.DataPart).Descendants(A + "t").Select(t => t.Value).SequenceEqual(text.Values<string>())).ToList();
            return targets;
        }

        private static bool ProtectedCommentExists(PackageSnapshot package, XDocument document, JObject item)
        {
            string anchor = RequiredObjectString(item, "paragraph");
            var paragraphs = document.Root.Element(W + "body").Descendants(W + "p").Where(p => ParagraphText(p) == anchor).ToList();
            if (paragraphs.Count != 1) return false;
            var ids = ActiveCommentIds(paragraphs[0], anchor);
            XDocument comments;
            return package.TryXml("word/comments.xml", out comments) && comments.Root.Elements(W + "comment")
                .Any(c => ids.Contains((string)c.Attribute(W + "id")) && VisibleText(c) == RequiredObjectString(item, "text"));
        }

        private static Dictionary<string, string> EffectiveParagraphFormatting(PackageSnapshot package, XElement paragraph)
        {
            var styles = package.Xml("word/styles.xml").Root;
            var properties = new XElement(W + "pPr");
            string style = (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "Normal";
            var defaults = styles.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr");
            foreach (var source in new[] { defaults }.Concat(StyleChain(styles, style).Select(s => s.Element(W + "pPr")))
                .Concat(new[] { paragraph.Element(W + "pPr") }).Where(p => p != null))
                foreach (var element in source.Elements())
                {
                    // Bookmarks, proofing and revision session IDs are not formatting.
                    if (new[] { "pStyle", "rPr", "sectPr", "pPrChange" }.Contains(element.Name.LocalName)) continue;
                    var existing = properties.Element(element.Name);
                    if (existing == null) properties.Add(new XElement(element));
                    else foreach (var attribute in element.Attributes()) existing.SetAttributeValue(attribute.Name, attribute.Value);
                }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in properties.Elements())
            {
                if (new[] { "keepNext", "keepLines", "pageBreakBefore", "widowControl", "contextualSpacing", "suppressAutoHyphens" }.Contains(e.Name.LocalName))
                { if (IsOn(e)) result[e.Name.LocalName] = "1"; }
                else result[e.Name.LocalName] = string.Join(";", e.Attributes().Where(a => a.Name.Namespace == W).OrderBy(a => a.Name.LocalName).Select(a => a.Name.LocalName + "=" + a.Value));
            }
            result["jc"] = "val=" + EffectiveParagraphAlignment(package, paragraph);
            return result;
        }

        private static bool FormattingEquals(IDictionary<string, string> actual, JObject expected)
        {
            return actual.Count == expected.Count && expected.Properties().All(p => actual.ContainsKey(p.Name) && actual[p.Name] == (string)p.Value);
        }

        private static bool CheckCopiedFormatting(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var paragraphs = MainParagraphs(package);
            var expectedRun = task.Extra["expectedRunFormatting"] as JObject;
            var expectedParagraph = task.Extra["expectedParagraphFormatting"] as JObject;
            if (expectedRun == null || expectedParagraph == null) throw new InvalidDataException("Missing verified copied formatting metadata.");
            foreach (string key in new[] { "sourceParagraph", "destinationParagraph" })
            {
                var targets = paragraphs.Where(p => ParagraphText(p) == RequiredString(task, key)).ToList();
                if (targets.Count != 1 || !FormattingEquals(EffectiveParagraphFormatting(package, targets[0]), expectedParagraph))
                { detail = "Source/destination paragraph text or effective paragraph formatting differs."; return false; }
                var runs = targets[0].Descendants(W + "r").Where(r => !r.Ancestors(W + "del").Any() && r.Elements(W + "t").Any(t => t.Value.Length > 0)).ToList();
                if (runs.Count == 0 || runs.Any(r => !FormattingEquals(EffectiveRunFormatting(package, targets[0], r), expectedRun)))
                { detail = "The complete visible text does not have the verified copied effective formatting."; return false; }
            }
            detail = "Source and destination preserve their text and verified complete run/paragraph formatting.";
            return true;
        }

        private static bool CheckColumnBreak(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var document = package.Xml("word/document.xml");
            var paragraphs = document.Root.Element(W + "body").Elements(W + "p").ToList();
            var targets = paragraphs.Where(p => ParagraphText(p) == RequiredString(task, "targetParagraph")).ToList();
            if (targets.Count != 1) { detail = "The unchanged target paragraph was not found uniquely."; return false; }
            var target = targets[0];
            var section = DocumentSections(document).Where(s => s.Content.Contains(target)).ToList();
            if (section.Count != 1 || ColumnCount(section[0].Properties) != RequiredInt(task, "expectedColumns"))
            { detail = "The target paragraph is not in the declared multi-column section."; return false; }
            var beforeText = target.Descendants().TakeWhile(e => e.Name != W + "t" || e.Value.Length == 0).ToList();
            var breaks = beforeText.Where(e => e.Name == W + "br").ToList();
            bool match = breaks.Count == 1 && (string)breaks[0].Attribute(W + "type") == "column";
            if (!match && breaks.Count == 0)
            {
                var previous = target.ElementsBeforeSelf().LastOrDefault();
                if (previous?.Name == W + "p")
                {
                    var afterText = previous.Descendants().Reverse().TakeWhile(e => e.Name != W + "t" || e.Value.Length == 0).ToList();
                    var previousBreaks = afterText.Where(e => e.Name == W + "br").ToList();
                    match = previousBreaks.Count == 1 && (string)previousBreaks[0].Attribute(W + "type") == "column";
                }
            }
            if (match && task.Extra.ContainsKey("sectionParagraphs"))
                match = section[0].Content.Where(e => e.Name == W + "p").Select(ParagraphText).Where(t => t.Length > 0).SequenceEqual(RequiredStrings(task, "sectionParagraphs"));
            detail = match ? "A real column break is immediately before the unchanged paragraph in the declared columns."
                : "The column break has the wrong type, position or section scope.";
            return match;
        }
    }
}
