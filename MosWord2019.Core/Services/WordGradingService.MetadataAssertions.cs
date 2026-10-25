using System;
using System.Linq;
using System.Xml.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static bool CheckPersonalInformationRemoved(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            detail = "The complete native personal-information category result, including save privacy flags, is required.";
            if (!new[] { "docProps/core.xml", "docProps/app.xml", "word/settings.xml" }.All(package.EntryNames.Contains)) return false;
            var core = package.Xml("docProps/core.xml").Root;
            var app = package.Xml("docProps/app.xml").Root;
            XNamespace ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
            foreach (string field in RequiredStrings(task, "corePropertiesToClear"))
            {
                var parts = field.Split(':');
                if (parts.Length != 2 || (parts[0] != "cp" && parts[0] != "dc"))
                    throw new System.IO.InvalidDataException("Invalid personal-information property mapping.");
                XNamespace ns = parts[0] == "cp" ? Cp : Dc;
                if (core.Elements(ns + parts[1]).Any(e => !string.IsNullOrWhiteSpace(e.Value))) return false;
            }
            if (RequiredStrings(task, "appPropertiesToClear").Any(name => app.Elements(ep + name).Any(e => !string.IsNullOrWhiteSpace(e.Value)))) return false;
            if (!RequiredStrings(task, "allowedTemplateNames").Contains((string)app.Element(ep + "Template") ?? "") ||
                app.Elements(ep + "TotalTime").Any(e => e.Value != "0")) return false;
            if (package.EntryNames.Contains("docProps/custom.xml") && package.Xml("docProps/custom.xml").Root.Elements().Any()) return false;
            var settings = package.Xml("word/settings.xml").Root;
            foreach (string flag in RequiredStrings(task, "requiredPrivacyFlags"))
            {
                if (flag != "removePersonalInformation" && flag != "removeDateAndTime")
                    throw new System.IO.InvalidDataException("Unsupported document-local privacy flag.");
                if (!IsOn(settings.Element(W + flag))) return false;
            }
            foreach (string part in package.EntryNames.Where(n => n.StartsWith("word/", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal)))
                foreach (var record in package.Xml(part).Descendants().Where(e => e.Attribute(W + "author") != null))
                    if (!new[] { "", "Author" }.Contains((string)record.Attribute(W + "author")) ||
                        !new[] { "", "A" }.Contains((string)record.Attribute(W + "initials") ?? "") ||
                        record.Attributes().Any(a => a.Name.LocalName == "date" || a.Name.LocalName == "dateUtc")) return false;
            return true;
        }

        private static bool CheckTableAltText(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var tables = FindTablesByHeader(package.Xml("word/document.xml"), RequiredStrings(task, "targetHeaderRow"));
            bool match = tables.Count == 1 && TableRowsEqual(tables[0], RequiredStringMatrix(task, "expectedTableRows"));
            if (match)
            {
                var table = tables[0];
                var properties = table.Element(W + "tblPr");
                match = properties?.Elements(W + "tblCaption").Count() == 1 && properties.Elements(W + "tblDescription").Count() == 1 &&
                    (string)properties.Element(W + "tblCaption")?.Attribute(W + "val") == RequiredString(task, "expectedTitle") &&
                    (string)properties.Element(W + "tblDescription")?.Attribute(W + "val") == RequiredString(task, "expectedDescription") &&
                    !table.Descendants().Any(e => e.Name == W + "gridSpan" || e.Name == W + "vMerge" || e.Name == W + "hMerge") &&
                    table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() == RequiredInt(task, "expectedColumns");
            }
            detail = match ? "The unchanged semantic table has both exact native Alt Text properties."
                : "The required table, content, Title or Description differs.";
            return match;
        }

        private static bool PreservedBookmarksMatch(XDocument document, TaskDefinition task)
        {
            if (!task.Extra.ContainsKey("preservedBookmarks")) return true;
            var ordered = document.Descendants().ToList();
            foreach (JObject expected in RequiredArray(task, "preservedBookmarks"))
            {
                var starts = ordered.Where(e => e.Name == W + "bookmarkStart" && (string)e.Attribute(W + "name") == (string)expected["name"]).ToList();
                if (starts.Count != 1) return false;
                var start = starts[0];
                string id = (string)start.Attribute(W + "id");
                var ends = ordered.Where(e => e.Name == W + "bookmarkEnd" && (string)e.Attribute(W + "id") == id).ToList();
                var paragraph = start.Ancestors(W + "p").FirstOrDefault();
                if (id == null || ends.Count != 1 || paragraph == null || ParagraphText(paragraph) != (string)expected["paragraph"]) return false;
                if (expected["precedingParagraph"] != null && PreviousNonemptyParagraph(paragraph) != (string)expected["precedingParagraph"]) return false;
                int first = ordered.IndexOf(start), last = ordered.IndexOf(ends[0]);
                var range = ordered.Skip(first + 1).Take(Math.Max(0, last - first - 1)).ToList();
                int offset = paragraph.Descendants().TakeWhile(e => e != start).Where(e => e.Name == W + "t").Sum(e => e.Value.Length);
                if (last <= first || offset != (int)expected["startOffset"] ||
                    string.Concat(range.Where(e => e.Name == W + "t").Select(e => e.Value)) != (string)expected["text"]) return false;
                if ((string)expected["text"] == "" && (ends[0].Ancestors(W + "p").FirstOrDefault() != paragraph ||
                    range.Any(e => e.Name == W + "tab" || e.Name == W + "br" || e.Name == W + "drawing"))) return false;
            }
            return true;
        }

        private static bool PreservedParagraphFormatsMatch(PackageSnapshot package, TaskDefinition task)
        {
            if (!task.Extra.ContainsKey("preservedParagraphFormats")) return true;
            foreach (JObject expected in RequiredArray(task, "preservedParagraphFormats"))
            {
                var paragraphs = MainParagraphs(package).Where(p => !p.Ancestors(W + "tbl").Any() &&
                    ParagraphTextOutsideTextBoxes(p) == (string)expected["text"]).ToList();
                if (paragraphs.Count != 1) return false;
                var paragraph = paragraphs[0];
                var requiredRun = (JObject)expected["runFormatting"];
                var requiredParagraph = (JObject)expected["paragraphFormatting"];
                if (requiredRun == null || requiredParagraph == null) throw new System.IO.InvalidDataException("Missing protected effective formatting.");
                var actualParagraph = EffectiveParagraphFormatting(package, paragraph);
                if (actualParagraph.Count != requiredParagraph.Count || requiredParagraph.Properties().Any(p =>
                    !actualParagraph.ContainsKey(p.Name) || actualParagraph[p.Name] != (string)p.Value)) return false;
                foreach (var run in paragraph.Descendants(W + "r").Where(r => !r.Ancestors(W + "txbxContent").Any() && r.Descendants(W + "t").Any(t => t.Value.Length > 0)))
                {
                    var actual = EffectiveRunFormatting(package, paragraph, run);
                    if (actual.Count != requiredRun.Count || requiredRun.Properties().Any(p => !actual.ContainsKey(p.Name) || actual[p.Name] != (string)p.Value)) return false;
                }
            }
            return true;
        }
    }
}
