using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Core.Services
{
    public sealed class WordGradingService
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

        private static readonly HashSet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "DocumentStyleSet", "BulletedList", "Footnote", "HeaderDifferentFirstPage",
            "SymbolInserted", "PictureArtisticEffect", "TableCellsMerged", "PictureWrapType"
        };

        public bool IsAssertionTypeSupported(string assertionType)
        {
            return !string.IsNullOrWhiteSpace(assertionType) && Supported.Contains(assertionType);
        }

        public TaskGradeResult CheckTask(string documentPath, TaskDefinition task)
        {
            if (task == null) return Error(null, "Task metadata is missing.");
            if (!IsAssertionTypeSupported(task.AssertionType)) return Error(task, "Unsupported assertion type: " + task.AssertionType);
            try
            {
                using (var package = PackageSnapshot.Open(documentPath))
                {
                    bool passed;
                    string detail;
                    switch (task.AssertionType)
                    {
                        case "DocumentStyleSet": passed = CheckDocumentStyleSet(package, task, out detail); break;
                        case "BulletedList": passed = CheckBulletedList(package, task, out detail); break;
                        case "Footnote": passed = CheckFootnote(package, task, out detail); break;
                        case "HeaderDifferentFirstPage": passed = CheckHeader(package, task, out detail); break;
                        case "SymbolInserted": passed = CheckSymbol(package, task, out detail); break;
                        case "PictureArtisticEffect": passed = CheckPictureEffect(package, task, out detail); break;
                        case "TableCellsMerged": passed = CheckMergedCells(package, task, out detail); break;
                        case "PictureWrapType": passed = CheckPictureWrap(package, task, out detail); break;
                        default: return Error(task, "Unsupported assertion type: " + task.AssertionType);
                    }
                    return new TaskGradeResult(passed ? TaskGradeOutcome.Pass : TaskGradeOutcome.Fail,
                        detail, task.AssertionType, task.TaskId);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
                                       ex is System.Xml.XmlException || ex is ArgumentException)
            {
                return Error(task, "Unable to inspect the saved Word document: " + ex.Message);
            }
        }

        private static TaskGradeResult Error(TaskDefinition task, string message)
        {
            return new TaskGradeResult(TaskGradeOutcome.Error, message,
                task == null ? "" : task.AssertionType, task == null ? "" : task.TaskId);
        }

        private static bool CheckDocumentStyleSet(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string[] styleIds = RequiredStrings(task, "styleIds");
            string expected = RequiredString(task, "expectedStyleSignature");
            XDocument styles = package.Xml("word/styles.xml");
            var byId = styles.Root.Elements(W + "style").Where(s => s.Attribute(W + "styleId") != null)
                .ToDictionary(s => (string)s.Attribute(W + "styleId"), StringComparer.Ordinal);
            string[] missing = styleIds.Where(id => !byId.ContainsKey(id)).ToArray();
            if (missing.Length > 0) { detail = "Required document styles are missing: " + string.Join(", ", missing); return false; }
            string actual = HashText(string.Join("\n", styleIds.OrderBy(x => x, StringComparer.Ordinal)
                .Select(id => id + "=" + Canonical(byId[id]))));
            bool match = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            detail = match ? "The document style-set signature matches." : "Style-set signature mismatch. Actual: " + actual;
            return match;
        }

        private static bool CheckBulletedList(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string[] targets = RequiredStrings(task, "targetParagraphs");
            int expectedBullet = RequiredInt(task, "expectedBulletPositionTwips");
            int expectedText = RequiredInt(task, "expectedTextIndentTwips");
            XDocument document = package.Xml("word/document.xml");
            XDocument numbering = package.Xml("word/numbering.xml");
            var paragraphs = document.Descendants(W + "body").Elements().Where(e => e.Name == W + "p").ToList();
            var indexes = new List<int>();
            foreach (string target in targets)
            {
                int index = paragraphs.FindIndex(p => VisibleText(p).StartsWith(target, StringComparison.Ordinal));
                if (index < 0) { detail = "Target paragraph is missing: " + target; return false; }
                indexes.Add(index);
            }
            if (indexes.Distinct().Count() != targets.Length || indexes.Where((value, i) => i > 0 && value != indexes[i - 1] + 1).Any())
            { detail = "The target paragraphs are not one exact consecutive block."; return false; }

            var numMap = numbering.Root.Elements(W + "num").Where(n => n.Attribute(W + "numId") != null)
                .ToDictionary(n => (string)n.Attribute(W + "numId"), n => (string)n.Element(W + "abstractNumId")?.Attribute(W + "val"), StringComparer.Ordinal);
            var abstracts = numbering.Root.Elements(W + "abstractNum").Where(n => n.Attribute(W + "abstractNumId") != null)
                .ToDictionary(n => (string)n.Attribute(W + "abstractNumId"), StringComparer.Ordinal);
            foreach (int index in indexes)
            {
                XElement pPr = paragraphs[index].Element(W + "pPr");
                XElement numPr = pPr?.Element(W + "numPr");
                string numId = (string)numPr?.Element(W + "numId")?.Attribute(W + "val");
                string level = (string)numPr?.Element(W + "ilvl")?.Attribute(W + "val") ?? "0";
                string abstractId;
                XElement abstractNum;
                if (string.IsNullOrEmpty(numId) || !numMap.TryGetValue(numId, out abstractId) || string.IsNullOrEmpty(abstractId) || !abstracts.TryGetValue(abstractId, out abstractNum))
                { detail = "A target paragraph is not connected to a valid numbering definition."; return false; }
                XElement lvl = abstractNum.Elements(W + "lvl").FirstOrDefault(x => ((string)x.Attribute(W + "ilvl") ?? "0") == level);
                if ((string)lvl?.Element(W + "numFmt")?.Attribute(W + "val") != "bullet")
                { detail = "A target paragraph does not use bullet numbering."; return false; }
                XElement directInd = pPr.Element(W + "ind");
                XElement levelInd = lvl?.Element(W + "pPr")?.Element(W + "ind");
                int left = Twips(directInd, "left", Twips(levelInd, "left", 0));
                int hanging = Twips(directInd, "hanging", Twips(levelInd, "hanging", 0));
                if (left != expectedText || left - hanging != expectedBullet)
                { detail = "Bullet position or hanging/text indent does not match the required geometry."; return false; }
            }
            if ((indexes[0] > 0 && HasNumbering(paragraphs[indexes[0] - 1])) ||
                (indexes[indexes.Count - 1] + 1 < paragraphs.Count && HasNumbering(paragraphs[indexes[indexes.Count - 1] + 1])))
            { detail = "A paragraph outside the requested five-item block is included in the list."; return false; }
            detail = "The exact target paragraphs form a bullet list with the required geometry.";
            return true;
        }

        private static bool CheckFootnote(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string anchor = RequiredString(task, "anchorText");
            string expected = RequiredString(task, "expectedText");
            XDocument document = package.Xml("word/document.xml");
            XDocument footnotes = package.Xml("word/footnotes.xml");
            List<XElement> candidates = document.Descendants(W + "p").Where(p => VisibleText(p) == anchor).ToList();
            if (candidates.Count != 1) { detail = "The exact footnote anchor heading was not found uniquely."; return false; }
            XElement paragraph = candidates[0];
            XElement reference = paragraph.Descendants(W + "footnoteReference").SingleOrDefault();
            if (reference == null) { detail = "No footnote reference follows the target heading."; return false; }
            string id = (string)reference.Attribute(W + "id");
            XElement footnote = footnotes.Root.Elements(W + "footnote").SingleOrDefault(f => (string)f.Attribute(W + "id") == id);
            if (footnote == null || VisibleText(footnote).TrimStart() != expected)
            { detail = "The target footnote text does not match exactly."; return false; }
            List<XElement> ordered = paragraph.Descendants().Where(e => e.Name == W + "t" || e.Name == W + "footnoteReference").ToList();
            int referenceIndex = ordered.IndexOf(reference);
            string before = string.Concat(ordered.Take(referenceIndex).Where(e => e.Name == W + "t").Select(e => e.Value));
            if (before != anchor || ordered.Skip(referenceIndex + 1).Any(e => e.Name == W + "t" && e.Value.Length > 0))
            { detail = "The footnote reference is not immediately after the target heading."; return false; }
            detail = "The exact footnote is attached immediately after the target heading.";
            return true;
        }

        private static bool CheckHeader(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            XDocument document = package.Xml("word/document.xml");
            XElement section = document.Root?.Element(W + "body")?.Element(W + "sectPr");
            if (section?.Element(W + "titlePg") == null) { detail = "Different First Page is not enabled."; return false; }
            if (section.Elements(W + "headerReference").Any(h => (string)h.Attribute(W + "type") == "first"))
            { detail = "A first-page header is present; page 1 must remain without Integral."; return false; }
            XElement primary = section.Elements(W + "headerReference").SingleOrDefault(h => ((string)h.Attribute(W + "type") ?? "default") == "default");
            string part = package.RelatedPart("word/document.xml", (string)primary?.Attribute(R + "id"));
            if (string.IsNullOrEmpty(part)) { detail = "The primary header is missing."; return false; }
            string expected = RequiredString(task, "expectedHeaderSignature");
            string actual = HashText(Canonical(package.Xml(part).Root));
            bool match = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            detail = match ? "Different First Page and the Integral header signature match."
                : "The primary header is not the required Integral structure. Actual: " + actual;
            return match;
        }

        private static bool CheckSymbol(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string anchor = RequiredString(task, "anchorText");
            string font = RequiredString(task, "symbolFont");
            int code = RequiredInt(task, "symbolCode");
            string expectedChar = (0xF000 + code).ToString("X4", CultureInfo.InvariantCulture);
            XDocument document = package.Xml("word/document.xml");
            List<XElement> paragraphs = document.Descendants(W + "p").Where(p => VisibleText(p).StartsWith(anchor, StringComparison.Ordinal)).ToList();
            if (paragraphs.Count != 1 || VisibleText(paragraphs[0]) != RequiredString(task, "expectedSentence"))
            { detail = "The exact target sentence is missing or changed."; return false; }
            List<XElement> ordered = paragraphs[0].Descendants().Where(e => e.Name == W + "sym" || e.Name == W + "t").ToList();
            int textIndex = ordered.FindIndex(e => e.Name == W + "t" && e.Value.StartsWith(anchor, StringComparison.Ordinal));
            XElement symbol = textIndex > 0 ? ordered[textIndex - 1] : null;
            bool match = symbol != null && symbol.Name == W + "sym" &&
                string.Equals((string)symbol.Attribute(W + "font"), font, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)symbol.Attribute(W + "char"), expectedChar, StringComparison.OrdinalIgnoreCase);
            detail = match ? "The required symbol is immediately before the unchanged sentence."
                : "The required Webdings symbol is missing, incorrect, or in the wrong position.";
            return match;
        }

        private static bool CheckPictureEffect(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            XElement drawing = FindPictureDrawing(package, RequiredStrings(task, "targetImageSha256s"));
            if (drawing == null) { detail = "The target picture cannot be identified by its source fingerprint."; return false; }
            string effect = RequiredString(task, "expectedEffectElement");
            bool match = drawing.Descendants().Any(e => e.Name.LocalName == effect && e.Name.NamespaceName == "http://schemas.microsoft.com/office/drawing/2010/main");
            detail = match ? "The target picture has the required artistic effect." : "The target picture does not have the required artistic effect.";
            return match;
        }

        private static bool CheckMergedCells(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string anchor = RequiredString(task, "tableAnchor");
            int expectedSpan = RequiredInt(task, "expectedColumnSpan");
            XDocument document = package.Xml("word/document.xml");
            List<XElement> tables = document.Descendants(W + "tbl").Where(t => VisibleText(t).Contains(anchor)).ToList();
            if (tables.Count != 1) { detail = "The target table was not found uniquely."; return false; }
            XElement table = tables[0];
            int columns = table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() ?? 0;
            List<XElement> rows = table.Elements(W + "tr").ToList();
            List<XElement> firstCells = rows.FirstOrDefault()?.Elements(W + "tc").ToList();
            if (columns != expectedSpan || firstCells == null || firstCells.Count != 1 ||
                Twips(firstCells[0].Element(W + "tcPr")?.Element(W + "gridSpan"), "val", 1) != expectedSpan || !VisibleText(firstCells[0]).Contains(anchor))
            { detail = "The complete first row is not merged across the required columns."; return false; }
            if (rows.Skip(1).Any(row => row.Elements(W + "tc").Count() != columns || row.Descendants(W + "gridSpan").Any() || row.Descendants(W + "vMerge").Any()))
            { detail = "An unrelated table row was merged or changed structurally."; return false; }
            detail = "Only the full first row of the target table is merged.";
            return true;
        }

        private static bool CheckPictureWrap(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            XElement drawing = FindPictureDrawing(package, new[] { RequiredString(task, "targetImageSha256") });
            if (drawing == null) { detail = "The target picture cannot be identified by its source fingerprint."; return false; }
            XElement wrapper = drawing.DescendantsAndSelf().SelectMany(e => e.AncestorsAndSelf())
                .FirstOrDefault(e => e.Name == Wp + "anchor" || e.Name == Wp + "inline");
            bool match = wrapper != null && wrapper.Name == Wp + "anchor" && wrapper.Element(Wp + "wrapSquare") != null;
            detail = match ? "The target picture uses Square wrapping." : "The target picture does not use Square wrapping.";
            return match;
        }

        private static XElement FindPictureDrawing(PackageSnapshot package, IEnumerable<string> expectedHashes)
        {
            var hashes = new HashSet<string>(expectedHashes, StringComparer.OrdinalIgnoreCase);
            XDocument document = package.Xml("word/document.xml");
            foreach (XElement blip in document.Descendants(A + "blip"))
            {
                string part = package.RelatedPart("word/document.xml", (string)blip.Attribute(R + "embed"));
                if (!string.IsNullOrEmpty(part) && hashes.Contains(package.Hash(part)))
                    return blip.Ancestors(W + "drawing").FirstOrDefault();
            }
            return null;
        }

        private static bool HasNumbering(XElement paragraph) { return paragraph.Element(W + "pPr")?.Element(W + "numPr") != null; }

        private static int Twips(XElement element, string attribute, int fallback)
        {
            int value;
            return element != null && int.TryParse((string)element.Attribute(W + attribute), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static string VisibleText(XElement element)
        {
            return string.Concat(element.Descendants(W + "t").Where(t => !t.Ancestors(W + "del").Any()).Select(t => t.Value));
        }

        private static string RequiredString(TaskDefinition task, string name)
        {
            JToken token;
            string value = task.Extra != null && task.Extra.TryGetValue(name, out token) ? (string)token : null;
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing grading metadata: " + name);
            return value;
        }

        private static string[] RequiredStrings(TaskDefinition task, string name)
        {
            JToken token;
            JArray array = task.Extra != null && task.Extra.TryGetValue(name, out token) ? token as JArray : null;
            string[] values = array?.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (values == null || values.Length == 0) throw new InvalidDataException("Missing grading metadata: " + name);
            return values;
        }

        private static int RequiredInt(TaskDefinition task, string name)
        {
            JToken token;
            int value;
            if (task.Extra == null || !task.Extra.TryGetValue(name, out token) || !int.TryParse(token.ToString(), out value))
                throw new InvalidDataException("Missing grading metadata: " + name);
            return value;
        }

        private static string HashText(string value) { return HashBytes(Encoding.UTF8.GetBytes(value)); }
        private static string HashBytes(byte[] value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "");
        }

        private static string Canonical(XElement element)
        {
            var builder = new StringBuilder();
            AppendCanonical(builder, element);
            return builder.ToString();
        }

        private static void AppendCanonical(StringBuilder builder, XElement element)
        {
            if (element.Name == W + "rPrChange" || element.Name == W + "pPrChange" || element.Name == W + "sectPrChange") return;
            builder.Append('<').Append(element.Name.NamespaceName).Append('|').Append(element.Name.LocalName);
            foreach (XAttribute attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && !IsVolatile(a))
                .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal).ThenBy(a => a.Name.LocalName, StringComparer.Ordinal))
                builder.Append(' ').Append(attribute.Name.NamespaceName).Append('|').Append(attribute.Name.LocalName).Append('=').Append(attribute.Value);
            builder.Append('>');
            if (!element.HasElements && !(element.Name == W + "t" && element.Ancestors(W + "sdtContent").Any()))
                builder.Append(element.Value);
            foreach (XElement child in element.Elements()) AppendCanonical(builder, child);
            builder.Append("</").Append(element.Name.NamespaceName).Append('|').Append(element.Name.LocalName).Append('>');
        }

        private static bool IsVolatile(XAttribute attribute)
        {
            string name = attribute.Name.LocalName;
            return name.StartsWith("rsid", StringComparison.OrdinalIgnoreCase) || name == "paraId" || name == "textId" ||
                   name == "anchorId" || name == "editId" || name == "date" || name == "author" || name == "id" ||
                   attribute.Name.NamespaceName == R.NamespaceName;
        }

        private sealed class PackageSnapshot : IDisposable
        {
            private readonly MemoryStream memory;
            private readonly ZipArchive zip;
            private readonly Dictionary<string, byte[]> entries;

            private PackageSnapshot(MemoryStream memory, ZipArchive zip, Dictionary<string, byte[]> entries)
            { this.memory = memory; this.zip = zip; this.entries = entries; }

            public static PackageSnapshot Open(string path)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("The working document does not exist.", path);
                var memory = new MemoryStream();
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) source.CopyTo(memory);
                memory.Position = 0;
                var zip = new ZipArchive(memory, ZipArchiveMode.Read, true);
                var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    using (Stream stream = entry.Open())
                    using (var copy = new MemoryStream()) { stream.CopyTo(copy); entries[Normalize(entry.FullName)] = copy.ToArray(); }
                }
                return new PackageSnapshot(memory, zip, entries);
            }

            public XDocument Xml(string part)
            {
                byte[] bytes;
                if (!entries.TryGetValue(Normalize(part), out bytes)) throw new InvalidDataException("Required OOXML part is missing: " + part);
                using (var stream = new MemoryStream(bytes, false)) return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            }

            public string Hash(string part)
            {
                byte[] bytes;
                return entries.TryGetValue(Normalize(part), out bytes) ? HashBytes(bytes) : null;
            }

            public string RelatedPart(string sourcePart, string relationshipId)
            {
                if (string.IsNullOrWhiteSpace(relationshipId)) return null;
                string normalizedSource = Normalize(sourcePart);
                int slash = normalizedSource.LastIndexOf('/');
                string folder = slash < 0 ? "" : normalizedSource.Substring(0, slash + 1);
                string file = slash < 0 ? normalizedSource : normalizedSource.Substring(slash + 1);
                string relsPart = folder + "_rels/" + file + ".rels";
                XDocument rels = Xml(relsPart);
                XElement relationship = rels.Root.Elements(Rel + "Relationship").SingleOrDefault(x => (string)x.Attribute("Id") == relationshipId);
                string target = (string)relationship?.Attribute("Target");
                if (string.IsNullOrWhiteSpace(target)) return null;
                if (target.StartsWith("/", StringComparison.Ordinal)) return Normalize(target.Substring(1));
                var segments = new List<string>(folder.TrimEnd('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
                foreach (string segment in target.Replace('\\', '/').Split('/'))
                {
                    if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
                    else if (segment != "." && segment.Length > 0) segments.Add(segment);
                }
                return string.Join("/", segments);
            }

            private static string Normalize(string part) { return (part ?? "").Replace('\\', '/').TrimStart('/'); }
            public void Dispose() { zip.Dispose(); memory.Dispose(); }
        }
    }
}
