using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static string CaptionExpectedText(TaskDefinition task)
        {
            string label = RequiredString(task, "expectedCaptionLabel");
            string number = RequiredString(task, "expectedCaptionNumber");
            string suffix = RequiredString(task, "expectedCaptionSuffix");
            if (!Regex.IsMatch(number, @"^[1-9][0-9]*$"))
                throw new InvalidDataException("The expected caption number must be a positive decimal integer.");
            return label + " " + number + suffix;
        }

        private static bool CaptionSequenceCodeMatches(string code, string label, string number)
        {
            string identifier = "(?:" + Regex.Escape(label) + "|\"" + Regex.Escape(label) + "\")";
            return Regex.IsMatch(code ?? "", @"^\s*SEQ\s+" + identifier +
                @"(?:\s+(?:\\\*\s+(?:ARABIC|MERGEFORMAT)|\\r\s*" + Regex.Escape(number) + @"(?=\s|$)))*\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && Regex.Matches(code ?? "", @"\\r(?=\s|[0-9]|$)", RegexOptions.IgnoreCase).Count <= 1;
        }

        // Shared by the caption assertion and optional preservation of an independently added caption.
        private static bool CaptionParagraphMatches(PackageSnapshot package, XElement paragraph, TaskDefinition task)
        {
            string expected = CaptionExpectedText(task);
            if (paragraph == null || paragraph.Name != W + "p" || paragraph.Parent?.Name != W + "body" ||
                LogicalParagraphText(paragraph) != expected ||
                paragraph.Descendants().Any(e => e.Name == W + "drawing" || e.Name == W + "pict" || e.Name == W + "object" ||
                    e.Name == W + "tab" || e.Name == W + "br" || e.Name == W + "cr" || e.Name == W + "sym" ||
                    e.Name == W + "del" || e.Name == W + "moveFrom" || e.Name == W + "delText")) return false;
            if (!LetterParagraphContentVisible(package, paragraph, true)) return false;
            var fields = BodyFields(paragraph);
            if (fields.Count != 1 || !CaptionSequenceCodeMatches(fields[0].Code.ToString(), RequiredString(task, "expectedCaptionLabel"), RequiredString(task, "expectedCaptionNumber")) ||
                fields[0].Result.ToString() != RequiredString(task, "expectedCaptionNumber")) return false;
            var simple = paragraph.Descendants(W + "fldSimple").ToList();
            var markers = paragraph.Descendants(W + "fldChar").ToList();
            if (simple.Count == 1) return markers.Count == 0 && !paragraph.Descendants(W + "instrText").Any();
            if (simple.Count != 0 || !markers.Select(e => (string)e.Attribute(W + "fldCharType"))
                .SequenceEqual(new[] { "begin", "separate", "end" })) return false;
            var ordered = paragraph.Descendants().ToList();
            int first = ordered.IndexOf(markers[0]), split = ordered.IndexOf(markers[1]);
            return paragraph.Descendants(W + "instrText").All(e => ordered.IndexOf(e) > first && ordered.IndexOf(e) < split);
        }

        private static bool CheckTableCaptionWithSequence(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string expected = CaptionExpectedText(task);
            string following = RequiredString(task, "followingParagraph");
            var table = FindDeclaredTableExact(package, task);
            detail = "The original table needs one visible native Table sequence caption directly below it and before its unchanged following paragraph.";
            if (table == null || table.Parent?.Name != W + "body") return false;
            var body = table.Parent;
            var children = body.Elements().Where(e => e.Name != W + "bookmarkStart" && e.Name != W + "bookmarkEnd").ToList();
            int index = children.IndexOf(table);
            if (index + 2 >= children.Count || !CaptionParagraphMatches(package, children[index + 1], task) ||
                children[index + 2].Name != W + "p" || LogicalParagraphText(children[index + 2]) != following ||
                body.Descendants(W + "p").Count(p => LogicalParagraphText(p) == expected) != 1 ||
                BodyFields(body).Count(f => Regex.IsMatch(f.Code.ToString(), @"^\s*SEQ\s+(?:" +
                    Regex.Escape(RequiredString(task, "expectedCaptionLabel")) + @"|""" +
                    Regex.Escape(RequiredString(task, "expectedCaptionLabel")) + @""")(?:\s|$)", RegexOptions.IgnoreCase)) != 1)
                return false;
            detail = "The unchanged table has one visible native sequence caption with the exact label, number, suffix and below-table location.";
            return true;
        }

        private static bool CheckSemanticSectionOrientation(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var declared = RequiredArray(task, "originalSectionParagraphs");
            if (declared.Count < 2 || declared.Any(t => !(t is JObject) ||
                !new[] { "portrait", "landscape" }.Contains((string)t["orientation"]) ||
                !(t["paragraphs"] is JArray) || !t["paragraphs"].HasValues ||
                ((JArray)t["paragraphs"]).Any(p => p.Type != JTokenType.String && (!(p is JArray) || !p.HasValues ||
                    ((JArray)p).Any(v => v.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)v)))) ||
                ((JArray)t["paragraphs"]).Any(p => p.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)p))))
                throw new InvalidDataException("Original section metadata must declare orientations and complete paragraph strings or finite alternatives.");
            string anchor = RequiredString(task, "anchorText"), expected = RequiredString(task, "expectedOrientation");
            if (!new[] { "portrait", "landscape" }.Contains(expected)) throw new InvalidDataException("Unsupported semantic target orientation.");
            var table = FindDeclaredTableExact(package, task);
            detail = "The existing source boundary, original section membership, complete letter/table or page dimensions differ from the requested orientation.";
            if (table == null || table.Parent?.Name != W + "body") return false;
            var body = table.Parent;
            var sections = DocumentSections(table.Document);
            var sourceParagraphs = body.Elements(W + "p").ToList();
            var groups = new List<List<XElement>>();
            var physicalSections = new List<DocumentSection>();
            var selected = new HashSet<XElement>();
            int previousIndex = -1;
            foreach (JObject group in declared)
            {
                var paragraphs = new List<XElement>();
                foreach (var entry in (JArray)group["paragraphs"])
                {
                    var allowed = entry is JArray ? ((JArray)entry).Values<string>().ToArray() : new[] { (string)entry };
                    var matches = sourceParagraphs.Where(p => allowed.Contains(LogicalParagraphText(p))).ToList();
                    if (matches.Count != 1 || !selected.Add(matches[0]) || !LetterParagraphContentVisible(package, matches[0])) return false;
                    int index = sourceParagraphs.IndexOf(matches[0]);
                    if (index <= previousIndex) return false;
                    previousIndex = index;
                    paragraphs.Add(matches[0]);
                }
                var physical = sections.Where(s => s.Content.Contains(paragraphs[0])).ToList();
                if (physical.Count != 1 || paragraphs.Any(p => !physical[0].Content.Contains(p)) ||
                    !IsOrientation(physical[0].Properties, (string)group["orientation"]) ||
                    Twips(physical[0].Properties.Element(W + "pgSz"), "w", -1) <= 0 ||
                    Twips(physical[0].Properties.Element(W + "pgSz"), "h", -1) <= 0) return false;
                groups.Add(paragraphs);
                physicalSections.Add(physical[0]);
            }
            // A native cover can add sections before the source letter. The original sections themselves remain intact.
            int firstSection = sections.IndexOf(physicalSections[0]);
            if (!physicalSections.Select(s => sections.IndexOf(s)).SequenceEqual(Enumerable.Range(firstSection, declared.Count)) ||
                firstSection + declared.Count != sections.Count) return false;
            var targets = sourceParagraphs.Where(p => LogicalParagraphText(p) == anchor).ToList();
            if (targets.Count != 1) return false;
            var target = sections.SingleOrDefault(s => s.Content.Contains(targets[0]));
            if (target == null || !physicalSections.Contains(target) || !target.Content.Contains(table) ||
                target.Content.FirstOrDefault(e => e.Name != W + "bookmarkStart" && e.Name != W + "bookmarkEnd") != targets[0] ||
                !IsOrientation(target.Properties, expected)) return false;
            int targetIndex = physicalSections.IndexOf(target);
            if (targetIndex == 0 || groups[targetIndex][0] != targets[0]) return false;
            var previous = physicalSections[targetIndex - 1];
            var boundary = previous.Content.LastOrDefault();
            if (boundary?.Name != W + "p" || boundary.Element(W + "pPr")?.Element(W + "sectPr") != previous.Properties ||
                LogicalParagraphText(boundary).Length != 0) return false;
            detail = "The complete original section after the existing boundary is Landscape; the original preceding letter remains Portrait with coherent page dimensions.";
            return true;
        }
    }
}
