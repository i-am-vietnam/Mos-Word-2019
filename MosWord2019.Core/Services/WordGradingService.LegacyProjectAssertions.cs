using System;
using System.Collections.Generic;
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
        private static bool CheckModernWordFormat(PackageSnapshot package, string path, TaskDefinition task, out string detail)
        {
            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            string type = (string)package.Xml("[Content_Types].xml").Root.Elements(ct + "Override")
                .SingleOrDefault(e => (string)e.Attribute("PartName") == "/word/document.xml")?.Attribute("ContentType");
            int mode;
            bool match = new[] { ".docx", ".dotx" }.Contains(Path.GetExtension(path).ToLowerInvariant()) &&
                RequiredStrings(task, "allowedMainContentTypes").Contains(type) &&
                package.Xml("word/document.xml").Root.Name == W + "document" &&
                int.TryParse((string)package.Xml("word/settings.xml").Descendants(W + "compatSetting")
                    .SingleOrDefault(e => (string)e.Attribute(W + "name") == "compatibilityMode")?.Attribute(W + "val"), out mode) &&
                mode >= RequiredInt(task, "minimumCompatibilityMode");
            detail = match ? "The saved document is modern OOXML with the verified current compatibility mode."
                : "The saved document has not left legacy/older Compatibility Mode.";
            return match;
        }

        private static bool CheckHeaderTextEffect(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string expectedText = RequiredString(task, "headerText");
            string headerType = RequiredString(task, "headerType");
            string color = RequiredString(task, "expectedColor");
            JObject attributes = RequiredArray(task, "shadowAttributes").Single() as JObject;
            if (attributes == null) throw new InvalidDataException("Missing shadow attributes.");
            var parts = package.Xml("word/document.xml").Descendants(W + "headerReference")
                .Where(r => !r.Ancestors(W + "sectPrChange").Any() && (string)r.Attribute(W + "type") == headerType)
                .Select(r => package.RelatedPart("word/document.xml", (string)r.Attribute(R + "id"))).Distinct().ToList();
            bool match = parts.Count > 0;
            foreach (string part in parts)
            {
                var targets = package.Xml(part).Root.Elements(W + "p").Where(p => ParagraphTextOutsideTextBoxes(p) == expectedText).ToList();
                if (targets.Count != 1) { match = false; continue; }
                var runs = targets[0].Descendants(W + "r").Where(r => !r.Ancestors(W + "txbxContent").Any() &&
                    r.Elements(W + "t").Any(t => t.Value.Length > 0)).ToList();
                if (runs.Count == 0) match = false;
                foreach (var run in runs)
                {
                    var effective = EffectiveRunFormatting(package, targets[0], run);
                    string actualColor, shadowXml;
                    if (!effective.TryGetValue("color", out actualColor) || actualColor != color ||
                        !effective.TryGetValue((W14 + "shadow").ToString(), out shadowXml)) { match = false; continue; }
                    XElement shadow = XElement.Parse(shadowXml);
                    match &= attributes.Properties().All(a => (string)shadow.Attribute(W14 + a.Name) == (string)a.Value);
                    var shadowColor = shadow.Element(W14 + "srgbClr");
                    match &= (string)shadowColor?.Attribute(W14 + "val") == RequiredString(task, "shadowColor") &&
                        (string)shadowColor?.Element(W14 + "alpha")?.Attribute(W14 + "val") == RequiredString(task, "shadowAlpha");
                }
            }
            detail = match ? "The unchanged target header uses the verified fill and shadow preset on all text."
                : "The target header text, fill or shadow preset is incorrect.";
            return match;
        }

        private static bool CheckBookmarkStart(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var document = package.Xml("word/document.xml");
            string target = RequiredString(task, "targetParagraph"), name = RequiredString(task, "bookmarkName");
            var paragraphs = document.Root.Element(W + "body").Descendants(W + "p")
                .Where(p => !p.Ancestors(W + "txbxContent").Any() && ParagraphText(p) == target).ToList();
            var bookmarks = document.Descendants(W + "bookmarkStart").Where(b => (string)b.Attribute(W + "name") == name).ToList();
            bool match = paragraphs.Count == 1 && bookmarks.Count == 1 && bookmarks[0].Ancestors(W + "p").FirstOrDefault() == paragraphs[0];
            if (match)
            {
                string id = (string)bookmarks[0].Attribute(W + "id");
                var ends = document.Descendants(W + "bookmarkEnd").Where(e => (string)e.Attribute(W + "id") == id).ToList();
                var before = paragraphs[0].Descendants().TakeWhile(e => e != bookmarks[0]);
                match = id != null && ends.Count == 1 &&
                    !before.Any(e => e.Ancestors(W + "r").Any() &&
                        ((e.Name == W + "t" && e.Value.Length > 0) || e.Name == W + "tab" || e.Name == W + "br"));
                if (match && OptionalBool(task, "requireCollapsedRange", false))
                {
                    var range = document.Descendants().SkipWhile(e => e != bookmarks[0]).Skip(1).TakeWhile(e => e != ends[0]).ToList();
                    match = ends[0].Ancestors(W + "p").FirstOrDefault() == paragraphs[0] &&
                        document.Descendants().SkipWhile(e => e != bookmarks[0]).Contains(ends[0]) &&
                        !range.Any(e => (e.Name == W + "t" && e.Value.Length > 0) || e.Name == W + "tab" || e.Name == W + "br" || e.Name == W + "drawing");
                }
            }
            detail = match ? "The exact bookmark starts at logical offset zero of the unchanged target paragraph."
                : "The required bookmark is missing, misnamed, misplaced or the paragraph changed.";
            return match;
        }

        private sealed class BodyField
        {
            public readonly StringBuilder Code = new StringBuilder();
            public readonly StringBuilder Result = new StringBuilder();
            public readonly HashSet<XElement> ResultParagraphs = new HashSet<XElement>();
            public bool Separated;
        }

        private static List<BodyField> BodyFields(XElement body)
        {
            var result = new List<BodyField>();
            var stack = new Stack<BodyField>();
            foreach (var element in body.Descendants()
                .Where(e => !e.Ancestors(W + "txbxContent").Any() && !e.Ancestors(W + "del").Any()))
            {
                if (element.Name == W + "fldChar")
                {
                    string type = (string)element.Attribute(W + "fldCharType");
                    if (type == "begin") stack.Push(new BodyField());
                    else if (type == "separate" && stack.Count > 0) stack.Peek().Separated = true;
                    else if (type == "end" && stack.Count > 0) { var field = stack.Pop(); if (field.Separated) result.Add(field); }
                }
                else if (element.Name == W + "instrText" && stack.Count > 0 && !stack.Peek().Separated) stack.Peek().Code.Append(element.Value);
                else if (element.Name == W + "t")
                    foreach (var field in stack.Where(f => f.Separated))
                    {
                        field.Result.Append(element.Value);
                        var paragraph = element.Ancestors(W + "p").FirstOrDefault();
                        if (paragraph != null) field.ResultParagraphs.Add(paragraph);
                    }
                else if (element.Name == W + "fldSimple")
                {
                    var field = new BodyField { Separated = true };
                    field.Code.Append((string)element.Attribute(W + "instr"));
                    field.Result.Append(VisibleText(element));
                    var paragraph = element.Ancestors(W + "p").FirstOrDefault();
                    if (paragraph != null) field.ResultParagraphs.Add(paragraph);
                    result.Add(field);
                }
            }
            return result;
        }

        private static bool CheckTocLevels(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var fields = BodyFields(package.Xml("word/document.xml").Root.Element(W + "body"))
                .Where(f => Regex.IsMatch(f.Code.ToString(), @"^\s*TOC\b", RegexOptions.IgnoreCase)).ToList();
            bool match = fields.Count == 1;
            if (match)
            {
                string code = NormalizeFieldCode(fields[0].Code.ToString());
                var ranges = Regex.Matches(code, @"\\o\s+""([0-9]+-[0-9]+)""", RegexOptions.IgnoreCase);
                match = ranges.Count == 1 && ranges[0].Groups[1].Value == RequiredString(task, "headingRange") &&
                    !Regex.IsMatch(code, @"\\[tf]\b", RegexOptions.IgnoreCase) &&
                    RequiredStrings(task, "expectedEntryTitles").All(t => CountOccurrences(fields[0].Result.ToString(), t) == 1);
            }
            detail = match ? "A real automatic TOC is configured for the requested heading range and retains its generated entries."
                : "The TOC is static, missing, empty, or configured for an incorrect heading range.";
            return match;
        }

        private static bool CheckEndnoteConversion(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var document = package.Xml("word/document.xml");
            var references = document.Descendants(W + "endnoteReference").ToList();
            var expected = RequiredArray(task, "expectedNotes");
            XDocument endnotes = null;
            bool match = !document.Descendants(W + "footnoteReference").Any() && references.Count == expected.Count &&
                package.TryXml("word/endnotes.xml", out endnotes);
            if (!match) { detail = "All original footnotes must be represented as referenced endnotes."; return false; }
            var notes = endnotes.Root.Elements(W + "endnote").Where(n => (string)n.Attribute(W + "type") != "separator" &&
                (string)n.Attribute(W + "type") != "continuationSeparator" && (string)n.Attribute(W + "type") != "continuationNotice").ToList();
            XDocument footnotes;
            match &= notes.Count == expected.Count && references.Select(r => (string)r.Attribute(W + "id")).Distinct().Count() == references.Count;
            if (package.TryXml("word/footnotes.xml", out footnotes))
                match &= !footnotes.Root.Elements(W + "footnote").Any(n => n.Attribute(W + "type") == null);
            for (int i = 0; match && i < references.Count; i++)
            {
                var spec = expected[i] as JObject;
                if (spec == null) throw new InvalidDataException("Invalid note metadata.");
                string id = (string)references[i].Attribute(W + "id");
                var note = notes.SingleOrDefault(n => (string)n.Attribute(W + "id") == id);
                var paragraph = references[i].Ancestors(W + "p").FirstOrDefault();
                string before = string.Concat(paragraph.Descendants().TakeWhile(e => e != references[i]).Where(e => e.Name == W + "t").Select(e => e.Value));
                match = note != null && VisibleText(note).Trim() == RequiredObjectString(spec, "text") &&
                    before.EndsWith(RequiredObjectString(spec, "referenceAfter"), StringComparison.Ordinal);
            }
            detail = match ? "Every original note is preserved as an endnote at its original text anchor, with no user footnotes remaining."
                : "Note count, content or reference location is incorrect, or user footnotes remain.";
            return match;
        }
    }
}
