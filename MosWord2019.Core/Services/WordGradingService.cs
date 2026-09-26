using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
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
        private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        private static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
        private static readonly XNamespace Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        private static readonly XNamespace B = "http://schemas.openxmlformats.org/officeDocument/2006/bibliography";

        private static readonly HashSet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "DocumentStyleSet", "BulletedList", "Footnote", "HeaderDifferentFirstPage",
            "SymbolInserted", "PictureArtisticEffect", "TableCellsMerged", "PictureWrapType",
            "TextRemovedFromParagraph", "TextReplaceAll", "TextConvertedToTable", "AutomaticTableOfContents",
            "TextBoxTextEquals", "CommentDeletedAtText", "ParagraphLineSpacingExact", "CharacterStyleAppliedToParagraph",
            "TableFirstRowIsHeader", "SectionOrientationByAnchor", "TableColumnWidthsEqual",
            "CitationPlaceholderAtParagraphEnd", "SmartArtDirectionEquals", "SmartArtAltTextDescriptionEquals",
            "CorePropertyEquals", "ParagraphFormattingMatches"
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
                        case "TextRemovedFromParagraph": passed = CheckTextRemoved(package, task, out detail); break;
                        case "TextReplaceAll": passed = CheckTextReplaceAll(package, task, out detail); break;
                        case "TextConvertedToTable": passed = CheckTextConvertedToTable(package, task, out detail); break;
                        case "AutomaticTableOfContents": passed = CheckAutomaticTableOfContents(package, task, out detail); break;
                        case "TextBoxTextEquals": passed = CheckTextBoxText(package, task, out detail); break;
                        case "CommentDeletedAtText": passed = CheckCommentDeleted(package, task, out detail); break;
                        case "ParagraphLineSpacingExact": passed = CheckParagraphLineSpacing(package, task, out detail); break;
                        case "CharacterStyleAppliedToParagraph": passed = CheckCharacterStyle(package, task, out detail); break;
                        case "TableFirstRowIsHeader": passed = CheckTableFirstRowHeader(package, task, out detail); break;
                        case "SectionOrientationByAnchor": passed = CheckSectionOrientation(package, task, out detail); break;
                        case "TableColumnWidthsEqual": passed = CheckTableColumnWidths(package, task, out detail); break;
                        case "CitationPlaceholderAtParagraphEnd": passed = CheckCitationPlaceholder(package, task, out detail); break;
                        case "SmartArtDirectionEquals": passed = CheckSmartArtDirection(package, task, out detail); break;
                        case "SmartArtAltTextDescriptionEquals": passed = CheckSmartArtAltText(package, task, out detail); break;
                        case "CorePropertyEquals": passed = CheckCoreProperty(package, task, out detail); break;
                        case "ParagraphFormattingMatches": passed = CheckParagraphFormatting(package, task, out detail); break;
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
            JArray requirements = RequiredArray(task, "semanticStyleRequirements");
            XDocument styles = package.Xml("word/styles.xml");
            var byId = styles.Root.Elements(W + "style").Where(s => s.Attribute(W + "styleId") != null)
                .ToDictionary(s => (string)s.Attribute(W + "styleId"), StringComparer.Ordinal);
            foreach (JToken requirementToken in requirements)
            {
                JObject requirement = requirementToken as JObject;
                if (requirement == null) throw new InvalidDataException("Invalid semantic style requirement.");
                string styleId = RequiredObjectString(requirement, "styleId");
                string path = RequiredObjectString(requirement, "path");
                XElement style;
                if (!byId.TryGetValue(styleId, out style))
                { detail = "Required document style is missing: " + styleId; return false; }
                XElement property = style;
                foreach (string segment in path.Split('/'))
                {
                    property = property?.Element(W + segment);
                    if (property == null) break;
                }
                if (property == null)
                { detail = "The " + styleId + " style is missing required semantic property " + path + "."; return false; }
                JObject attributes = requirement["attributes"] as JObject;
                if (attributes != null && attributes.Properties().Any(attribute =>
                    !string.Equals((string)property.Attribute(W + attribute.Name), (string)attribute.Value, StringComparison.Ordinal)))
                { detail = "The " + styleId + " style property " + path + " does not match the requested style set."; return false; }
            }
            detail = "The document styles match the semantic properties of the requested style set.";
            return true;
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
            XElement first = section.Elements(W + "headerReference").SingleOrDefault(h => (string)h.Attribute(W + "type") == "first");
            string firstPart = package.RelatedPart("word/document.xml", (string)first?.Attribute(R + "id"));
            if (!string.IsNullOrEmpty(firstPart) && HeaderHasVisibleContent(package.Xml(firstPart).Root))
            { detail = "The first-page header contains visible content; page 1 must remain without Integral."; return false; }
            XElement primary = section.Elements(W + "headerReference").SingleOrDefault(h => ((string)h.Attribute(W + "type") ?? "default") == "default");
            string part = package.RelatedPart("word/document.xml", (string)primary?.Attribute(R + "id"));
            if (string.IsNullOrEmpty(part)) { detail = "The primary header is missing."; return false; }
            XElement header = package.Xml(part).Root;
            int expectedColumns = RequiredInt(task, "expectedHeaderTableColumns");
            string fill = RequiredString(task, "expectedHeaderFillColor");
            string themeFill = RequiredString(task, "expectedHeaderThemeFill");
            string alias = RequiredString(task, "expectedTitleControlAlias");
            XElement table = header?.Elements(W + "tbl").SingleOrDefault();
            XElement tableProperties = table?.Element(W + "tblPr");
            XElement shading = tableProperties?.Element(W + "shd");
            XElement titleControl = table?.Descendants(W + "sdt").SingleOrDefault(sdt =>
                string.Equals((string)sdt.Element(W + "sdtPr")?.Element(W + "alias")?.Attribute(W + "val"), alias, StringComparison.Ordinal));
            bool titleBinding = titleControl?.Element(W + "sdtPr")?.Element(W + "dataBinding")?.Attribute(W + "xpath")?.Value
                .IndexOf("coreProperties", StringComparison.Ordinal) >= 0;
            bool match = table != null && table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() == expectedColumns &&
                         string.Equals((string)shading?.Attribute(W + "fill"), fill, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals((string)shading?.Attribute(W + "themeFill"), themeFill, StringComparison.Ordinal) &&
                         titleControl != null && titleBinding;
            detail = match ? "Different First Page is enabled, page 1 is empty, and the primary header has the required Integral structure."
                : "The primary header is not the required Integral structure.";
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

        private static bool CheckTextRemoved(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string prefix = RequiredString(task, "targetParagraphStartsWith");
            string removed = RequiredString(task, "removedText");
            string expected = RequiredString(task, "expectedFinalText");
            int expectedRemaining = RequiredInt(task, "expectedRemainingCount");
            XDocument document = package.Xml("word/document.xml");
            List<XElement> candidates = document.Descendants(W + "body").Descendants(W + "p")
                .Where(p => ParagraphText(p).StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (candidates.Count != 1) { detail = "The target paragraph was not found uniquely."; return false; }
            if (!string.Equals(ParagraphText(candidates[0]), expected, StringComparison.Ordinal))
            { detail = "The target paragraph contains additional or incorrect text changes."; return false; }
            int remaining = CountOccurrences(VisibleText(document.Root), removed);
            bool match = remaining == expectedRemaining;
            detail = match ? "The requested text is removed and the target paragraph is otherwise unchanged."
                : "The removed text still occurs an unexpected number of times.";
            return match;
        }

        private static bool CheckTextReplaceAll(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string anchor = RequiredString(task, "tableAnchor");
            string oldText = RequiredString(task, "oldText");
            string newText = RequiredString(task, "newText");
            int expectedCount = RequiredInt(task, "expectedOriginalCount");
            string[][] expectedRows = RequiredStringMatrix(task, "expectedTableRows");
            XDocument document = package.Xml("word/document.xml");
            XElement body = document.Root?.Element(W + "body");
            List<XElement> children = body?.Elements().ToList() ?? new List<XElement>();
            int anchorIndex = children.FindIndex(e => e.Name == W + "p" && ParagraphTextOutsideTextBoxes(e) == anchor);
            XElement table = anchorIndex >= 0 ? children.Skip(anchorIndex + 1).FirstOrDefault(e => e.Name == W + "tbl" || e.Name == W + "p") : null;
            if (table == null || table.Name != W + "tbl") { detail = "The target table was not found immediately after its heading."; return false; }
            if (!TableRowsEqual(table, expectedRows))
            { detail = "The target table content or structure differs beyond the requested replacement."; return false; }
            string allText = VisibleText(document.Root);
            bool match = CountOccurrences(allText, oldText) == 0 && CountOccurrences(allText, newText) == expectedCount;
            detail = match ? "All requested occurrences are replaced and the target table is otherwise unchanged."
                : "The old text remains or the replacement count is incorrect.";
            return match;
        }

        private static bool CheckTextConvertedToTable(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string heading = RequiredString(task, "sectionHeading");
            string intro = RequiredString(task, "introParagraph");
            string following = RequiredString(task, "followingHeading");
            int expectedColumns = RequiredInt(task, "expectedColumns");
            int expectedRows = RequiredInt(task, "expectedRows");
            string[][] expectedCells = RequiredStringMatrix(task, "expectedTableRows");
            XDocument document = package.Xml("word/document.xml");
            XElement body = document.Root?.Element(W + "body");
            List<XElement> children = body?.Elements().ToList() ?? new List<XElement>();
            int headingIndex = children.FindIndex(e => e.Name == W + "p" && ParagraphText(e) == heading);
            int introIndex = children.FindIndex(headingIndex + 1, e => e.Name == W + "p" && ParagraphText(e) == intro);
            int followingIndex = children.FindIndex(Math.Max(0, introIndex + 1), e => e.Name == W + "p" && ParagraphTextOutsideTextBoxes(e) == following);
            if (headingIndex < 0 || introIndex != headingIndex + 1 || followingIndex < 0)
            { detail = "The Lecturers section anchors are missing or out of order."; return false; }
            List<XElement> between = children.Skip(introIndex + 1).Take(followingIndex - introIndex - 1).ToList();
            List<XElement> tables = between.Where(e => e.Name == W + "tbl").ToList();
            if (tables.Count != 1 || between.Any(e => e.Name == W + "p" && (ParagraphText(e).Contains("\t") || e.Descendants(W + "tab").Any())))
            { detail = "The tab-delimited source block was not replaced by one table in the required section."; return false; }
            XElement table = tables[0];
            int columns = table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() ?? 0;
            int rows = table.Elements(W + "tr").Count();
            if (columns != expectedColumns || rows != expectedRows || !TableRowsEqual(table, expectedCells))
            { detail = "The converted table does not match the verified Word table structure and content."; return false; }
            detail = "The complete tab-delimited block is the verified Word table in the Lecturers section.";
            return true;
        }

        private static bool CheckAutomaticTableOfContents(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string titleText = RequiredString(task, "documentTitle");
            string gallery = RequiredString(task, "expectedGallery");
            string headingText = RequiredString(task, "expectedHeadingText");
            string fieldCode = RequiredString(task, "expectedFieldCode");
            XDocument document = package.Xml("word/document.xml");
            XElement body = document.Root?.Element(W + "body");
            List<XElement> children = body?.Elements().ToList() ?? new List<XElement>();
            int titleIndex = children.FindIndex(e => e.Name == W + "p" && ParagraphText(e) == titleText);
            XElement sdt = titleIndex >= 0 && titleIndex + 1 < children.Count ? children[titleIndex + 1] : null;
            if (sdt == null || sdt.Name != W + "sdt") { detail = "A Word table-of-contents building block is not immediately after the document title."; return false; }
            XElement properties = sdt.Element(W + "sdtPr");
            XElement galleryElement = properties?.Element(W + "docPartObj")?.Element(W + "docPartGallery");
            bool unique = properties?.Element(W + "docPartObj")?.Element(W + "docPartUnique") != null;
            XElement heading = sdt.Element(W + "sdtContent")?.Elements(W + "p").FirstOrDefault();
            string headingStyle = (string)heading?.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");
            string actualField = sdt.Descendants(W + "instrText").Select(x => NormalizeFieldCode(x.Value))
                .FirstOrDefault(x => x.StartsWith("TOC ", StringComparison.Ordinal)) ?? "";
            bool hasComplexField = sdt.Descendants(W + "fldChar").Any(e => (string)e.Attribute(W + "fldCharType") == "begin") &&
                                   sdt.Descendants(W + "fldChar").Any(e => (string)e.Attribute(W + "fldCharType") == "end");
            bool match = string.Equals((string)galleryElement?.Attribute(W + "val"), gallery, StringComparison.Ordinal) && unique &&
                         heading != null && ParagraphText(heading) == headingText && headingStyle == "TOCHeading" &&
                         string.Equals(actualField, fieldCode, StringComparison.Ordinal) && hasComplexField;
            detail = match ? "The verified Automatic Table 1 structure is immediately after the title."
                : "The content is not the required Automatic Table 1 Word structure.";
            return match;
        }

        private static bool CheckTextBoxText(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string anchor = RequiredString(task, "anchorText");
            string fill = RequiredString(task, "fillColor");
            string geometry = RequiredString(task, "shapeGeometry");
            string expected = RequiredString(task, "expectedText");
            bool allowAutomaticUppercase = OptionalBool(task, "allowAutomaticUppercase", false);
            XDocument document = package.Xml("word/document.xml");
            List<TextBoxCandidate> matches = TextBoxCandidates(document)
                .Where(candidate => candidate.OuterParagraph != null &&
                    ParagraphTextOutsideTextBoxes(candidate.OuterParagraph) == anchor &&
                    string.Equals(NormalizeColor(candidate.FillColor), NormalizeColor(fill), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Geometry, geometry, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) { detail = "The target dark-blue text box was not found uniquely."; return false; }
            XElement textBox = matches[0].TextBox;
            string actual = textBox == null ? null : VisibleText(textBox).TrimEnd();
            bool match = string.Equals(actual, expected, StringComparison.Ordinal) ||
                         (allowAutomaticUppercase && string.Equals(actual, expected.ToUpperInvariant(), StringComparison.Ordinal));
            detail = match ? "The target dark-blue text box contains the exact requested text."
                : "The required exact text is missing from the target dark-blue text box.";
            return match;
        }

        private static IEnumerable<TextBoxCandidate> TextBoxCandidates(XDocument document)
        {
            foreach (XElement alternate in document.Descendants(Mc + "AlternateContent"))
            {
                XElement outer = alternate.Ancestors(W + "p").FirstOrDefault();
                List<TextBoxCandidate> drawing = alternate.Elements(Mc + "Choice")
                    .SelectMany(choice => choice.Descendants(Wps + "wsp"))
                    .Select(shape => DrawingTextBoxCandidate(shape, outer))
                    .Where(candidate => candidate != null).ToList();
                if (drawing.Count > 0)
                {
                    foreach (TextBoxCandidate candidate in drawing) yield return candidate;
                    continue;
                }

                XElement fallback = alternate.Element(Mc + "Fallback");
                if (fallback == null) continue;
                foreach (XElement shape in fallback.Descendants().Where(element => element.Name == V + "rect" || element.Name == V + "shape"))
                {
                    TextBoxCandidate candidate = VmlTextBoxCandidate(shape, outer);
                    if (candidate != null) yield return candidate;
                }
            }
        }

        private static TextBoxCandidate DrawingTextBoxCandidate(XElement shape, XElement outer)
        {
            XElement properties = shape.Element(Wps + "spPr");
            XElement textBox = shape.Element(Wps + "txbx")?.Element(W + "txbxContent");
            if (properties == null || textBox == null) return null;
            return new TextBoxCandidate(outer, textBox,
                (string)properties.Element(A + "prstGeom")?.Attribute("prst"),
                (string)properties.Element(A + "solidFill")?.Element(A + "srgbClr")?.Attribute("val"));
        }

        private static TextBoxCandidate VmlTextBoxCandidate(XElement shape, XElement outer)
        {
            XElement textBox = shape.Descendants(W + "txbxContent").FirstOrDefault();
            if (textBox == null) return null;
            string geometry = shape.Name == V + "rect" || string.Equals((string)shape.Attribute("type"), "#_x0000_t202", StringComparison.OrdinalIgnoreCase)
                ? "rect" : shape.Name.LocalName;
            return new TextBoxCandidate(outer, textBox, geometry, (string)shape.Attribute("fillcolor"));
        }

        private static string NormalizeColor(string value)
        {
            return (value ?? "").Trim().TrimStart('#');
        }

        private static bool HeaderHasVisibleContent(XElement header)
        {
            if (header == null) return false;
            if (header.Descendants(W + "t").Any(text => !string.IsNullOrWhiteSpace(text.Value))) return true;
            return header.Descendants().Any(element => element.Name == W + "tbl" || element.Name == W + "drawing" ||
                element.Name == W + "pict" || element.Name == W + "object" || element.Name == W + "sym" ||
                element.Name == W + "fldChar" || element.Name == W + "instrText");
        }

        private static bool CheckCommentDeleted(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string expectedParagraph = RequiredString(task, "expectedParagraph");
            string targetText = RequiredString(task, "targetText");
            XDocument document = package.Xml("word/document.xml");
            List<XElement> paragraphs = document.Descendants(W + "body").Descendants(W + "p")
                .Where(p => ParagraphText(p) == expectedParagraph).ToList();
            if (paragraphs.Count != 1) { detail = "The target paragraph or target word was changed."; return false; }
            bool active = HasActiveCommentOnText(paragraphs[0], targetText);
            detail = active ? "The comment attached to the target text is still active."
                : "The target text is preserved and its attached comment is deleted.";
            return !active;
        }

        private static bool CheckParagraphLineSpacing(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string[] prefixes = RequiredStrings(task, "targetParagraphStartsWith");
            int line = RequiredInt(task, "expectedLineTwips");
            string rule = RequiredString(task, "expectedLineRule");
            JArray before = RequiredArray(task, "expectedBeforeTwips");
            JArray after = RequiredArray(task, "expectedAfterTwips");
            XDocument document = package.Xml("word/document.xml");
            for (int i = 0; i < prefixes.Length; i++)
            {
                List<XElement> matches = document.Descendants(W + "body").Descendants(W + "p")
                    .Where(p => ParagraphText(p).StartsWith(prefixes[i], StringComparison.Ordinal)).ToList();
                if (matches.Count != 1) { detail = "A target paragraph was not found uniquely."; return false; }
                XElement spacing = matches[0].Element(W + "pPr")?.Element(W + "spacing");
                if (Twips(spacing, "line", -1) != line || (string)spacing?.Attribute(W + "lineRule") != rule ||
                    !NullableTwipsEquals(spacing, "before", before, i) || !NullableTwipsEquals(spacing, "after", after, i))
                { detail = "One or more target paragraphs do not use the exact verified spacing without changing before/after spacing."; return false; }
            }
            detail = "Both target paragraphs use Exactly 14 pt with their original before/after spacing.";
            return true;
        }

        private static bool CheckCharacterStyle(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string expectedParagraph = RequiredString(task, "expectedParagraph");
            string style = RequiredString(task, "characterStyleId");
            XDocument document = package.Xml("word/document.xml");
            List<XElement> paragraphs = document.Descendants(W + "body").Descendants(W + "p")
                .Where(p => ParagraphText(p) == expectedParagraph).ToList();
            if (paragraphs.Count != 1) { detail = "The target paragraph text is missing or changed."; return false; }
            List<XElement> visibleText = paragraphs[0].Descendants(W + "t")
                .Where(t => !t.Ancestors(W + "del").Any() && t.Value.Length > 0).ToList();
            bool match = visibleText.Count > 0 && visibleText.All(t =>
                string.Equals((string)t.Ancestors(W + "r").FirstOrDefault()?.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val"),
                    style, StringComparison.Ordinal));
            detail = match ? "The entire visible target paragraph uses the requested character style."
                : "The requested character style is missing from part or all of the target paragraph.";
            return match;
        }

        private static bool CheckTableFirstRowHeader(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string[] header = RequiredStrings(task, "targetHeaderRow");
            string[][] expectedRows = RequiredStringMatrix(task, "expectedTableRows");
            XDocument document = package.Xml("word/document.xml");
            List<XElement> tables = FindTablesByHeader(document, header);
            if (tables.Count != 1 || !TableRowsEqual(tables[0], expectedRows))
            { detail = "The target table is missing or its content was changed."; return false; }
            List<XElement> rows = tables[0].Elements(W + "tr").ToList();
            bool match = IsOn(rows[0].Element(W + "trPr")?.Element(W + "tblHeader")) &&
                rows.Skip(1).All(row => !IsOn(row.Element(W + "trPr")?.Element(W + "tblHeader")));
            detail = match ? "The target table uses its first row, and only its first row, as the repeating header."
                : "The target table's first row is not marked as its header.";
            return match;
        }

        private static bool CheckSectionOrientation(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string anchor = RequiredString(task, "anchorText");
            int expectedCount = RequiredInt(task, "expectedSectionCount");
            string expected = RequiredString(task, "expectedOrientation");
            bool requireOtherPortrait = OptionalBool(task, "requireOtherSectionsPortrait", false);
            List<DocumentSection> sections = DocumentSections(package.Xml("word/document.xml"));
            if (sections.Count != expectedCount)
            { detail = "The document section count differs from the verified structure."; return false; }
            List<DocumentSection> targets = sections.Where(section => section.Content
                .Any(element => element.Name == W + "p" && ParagraphTextOutsideCitations(element) == anchor)).ToList();
            if (targets.Count != 1)
            { detail = "The target page section was not found uniquely from its content anchor."; return false; }
            bool targetMatch = IsOrientation(targets[0].Properties, expected);
            bool othersMatch = !requireOtherPortrait || sections.Where(section => section != targets[0])
                .All(section => IsOrientation(section.Properties, "portrait"));
            bool match = targetMatch && othersMatch;
            detail = match ? "Only the section containing the target page is Landscape; all other sections remain Portrait."
                : "The target page orientation or another section orientation is incorrect.";
            return match;
        }

        private static bool CheckTableColumnWidths(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string[] header = RequiredStrings(task, "targetHeaderRow");
            string[][] expectedRows = RequiredStringMatrix(task, "expectedTableRows");
            int expectedWidth = RequiredInt(task, "expectedWidthTwips");
            int tolerance = RequiredInt(task, "widthToleranceTwips");
            bool noRowHeight = OptionalBool(task, "requireNoExplicitRowHeight", false);
            XDocument document = package.Xml("word/document.xml");
            List<XElement> tables = FindTablesByHeader(document, header);
            if (tables.Count != 1 || !TableRowsEqual(tables[0], expectedRows))
            { detail = "The target table is missing or its content was changed."; return false; }
            List<int> widths = tables[0].Element(W + "tblGrid")?.Elements(W + "gridCol")
                .Select(column => Twips(column, "w", -1)).ToList() ?? new List<int>();
            bool widthsMatch = widths.Count == header.Length && widths.All(width => Math.Abs(width - expectedWidth) <= tolerance);
            bool rowsMatch = !noRowHeight || tables[0].Elements(W + "tr")
                .All(row => row.Element(W + "trPr")?.Element(W + "trHeight") == null);
            bool match = widthsMatch && rowsMatch;
            detail = match ? "Every column in the target table has the verified 1.57-inch width and row heights remain unchanged."
                : "One or more target table columns have the wrong width, or row height was changed.";
            return match;
        }

        private static bool CheckCitationPlaceholder(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string targetText = RequiredString(task, "targetParagraph");
            string tag = RequiredString(task, "placeholderTag");
            string fieldCode = RequiredString(task, "expectedFieldCode");
            bool placeholderOnly = OptionalBool(task, "requirePlaceholderOnlySource", true);
            XDocument document = package.Xml("word/document.xml");
            List<XElement> paragraphs = document.Descendants(W + "body").Descendants(W + "p")
                .Where(item => ParagraphTextOutsideCitations(item) == targetText).ToList();
            if (paragraphs.Count != 1)
            { detail = "The target paragraph is missing or its text was changed."; return false; }
            XElement paragraph = paragraphs[0];
            List<XElement> citations = paragraph.Elements(W + "sdt")
                .Where(value => value.Element(W + "sdtPr")?.Element(W + "citation") != null).ToList();
            if (citations.Count != 1 || paragraph.Elements().LastOrDefault() != citations[0])
            { detail = "The required citation placeholder is missing from the end of the target paragraph."; return false; }
            XElement citation = citations[0];
            string actualField = NormalizeFieldCode(string.Concat(citation.Descendants(W + "instrText").Select(value => value.Value)));
            bool complexField = citation.Descendants(W + "fldChar").Any(value => (string)value.Attribute(W + "fldCharType") == "begin") &&
                citation.Descendants(W + "fldChar").Any(value => (string)value.Attribute(W + "fldCharType") == "end");
            var sources = new List<XElement>();
            foreach (string part in package.EntryNames.Where(name => name.StartsWith("customXml/item", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && name.IndexOf("itemProps", StringComparison.OrdinalIgnoreCase) < 0))
            {
                XDocument sourcePart;
                if (!package.TryXml(part, out sourcePart)) continue;
                sources.AddRange(sourcePart.Descendants(B + "Source").Where(source =>
                    string.Equals((string)source.Element(B + "Tag"), tag, StringComparison.Ordinal)));
            }
            bool sourceMatch = sources.Count == 1 && (!placeholderOnly || sources[0].Elements()
                .All(element => element.Name == B + "Tag" || element.Name == B + "RefOrder"));
            bool match = string.Equals(actualField, fieldCode, StringComparison.Ordinal) && complexField && sourceMatch;
            detail = match ? "The MOS placeholder citation is a real Word placeholder at the end of the target paragraph."
                : "The citation field or bibliography source is not the required MOS placeholder.";
            return match;
        }

        private static bool CheckSmartArtDirection(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string anchor = RequiredString(task, "anchorText");
            string direction = RequiredString(task, "expectedDirection");
            string[] expectedText = RequiredStrings(task, "expectedTextItems");
            List<SmartArtReference> smartArts = FindSmartArts(package, anchor);
            if (smartArts.Count != 1)
            { detail = "The target SmartArt was not found uniquely."; return false; }
            XDocument data = package.Xml(smartArts[0].DataPart);
            string actualDirection = (string)data.Descendants(Dgm + "dir").FirstOrDefault()?.Attribute("val");
            string[] actualText = data.Descendants(A + "t").Select(value => value.Value).ToArray();
            bool match = string.Equals(actualDirection, direction, StringComparison.Ordinal) && actualText.SequenceEqual(expectedText);
            detail = match ? "The target SmartArt is in the verified Right-to-Left state without changing its data items."
                : "The target SmartArt direction or its data-item order/text is incorrect.";
            return match;
        }

        private static bool CheckSmartArtAltText(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string anchor = RequiredString(task, "anchorText");
            string expected = RequiredString(task, "expectedDescription");
            List<SmartArtReference> smartArts = FindSmartArts(package, anchor);
            if (smartArts.Count != 1)
            { detail = "The target SmartArt was not found uniquely."; return false; }
            XElement properties = smartArts[0].Container.Element(Wp + "docPr");
            bool match = string.Equals((string)properties?.Attribute("descr"), expected, StringComparison.Ordinal) &&
                string.IsNullOrEmpty((string)properties?.Attribute("title"));
            detail = match ? "The entire target SmartArt has the exact requested alt-text description."
                : "The requested description is missing, differs, or was placed in the title field.";
            return match;
        }

        private static bool CheckCoreProperty(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string property = RequiredString(task, "propertyName");
            string expected = RequiredString(task, "expectedValue");
            XDocument core = package.Xml("docProps/core.xml");
            string actual = (string)core.Root?.Element(Cp + property);
            bool match = string.Equals(actual, expected, StringComparison.Ordinal);
            detail = match ? "The requested built-in file property has the exact value."
                : "The requested value is missing from the correct built-in file property.";
            return match;
        }

        private static bool CheckParagraphFormatting(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string sourceText = RequiredString(task, "sourceParagraph");
            string destinationText = RequiredString(task, "destinationParagraph");
            string alignment = RequiredString(task, "expectedAlignment");
            string style = RequiredString(task, "expectedCharacterStyle");
            XDocument document = package.Xml("word/document.xml");
            List<XElement> source = document.Descendants(W + "body").Descendants(W + "p")
                .Where(paragraph => ParagraphTextOutsideCitations(paragraph) == sourceText).ToList();
            List<XElement> destination = document.Descendants(W + "body").Descendants(W + "p")
                .Where(paragraph => ParagraphTextOutsideCitations(paragraph) == destinationText).ToList();
            if (source.Count != 1 || destination.Count != 1)
            { detail = "The source or destination paragraph text was changed."; return false; }
            bool match = ParagraphUsesFormatting(source[0], alignment, style) && ParagraphUsesFormatting(destination[0], alignment, style);
            detail = match ? "The destination paragraph has the verified full Format Painter result while preserving its text."
                : "The destination paragraph has only partial or incorrect copied formatting.";
            return match;
        }

        private static List<XElement> FindTablesByHeader(XDocument document, string[] header)
        {
            return document.Descendants(W + "body").Descendants(W + "tbl").Where(table =>
            {
                XElement first = table.Elements(W + "tr").FirstOrDefault();
                return first != null && first.Elements(W + "tc").Select(cell => VisibleText(cell).TrimEnd()).SequenceEqual(header);
            }).ToList();
        }

        private static bool IsOn(XElement value)
        {
            if (value == null) return false;
            string setting = (string)value.Attribute(W + "val");
            return string.IsNullOrEmpty(setting) || setting == "1" || setting == "true" || setting == "on";
        }

        private static bool IsOrientation(XElement section, string expected)
        {
            XElement size = section?.Element(W + "pgSz");
            int width = Twips(size, "w", -1);
            int height = Twips(size, "h", -1);
            string orientation = (string)size?.Attribute(W + "orient");
            if (string.Equals(expected, "landscape", StringComparison.OrdinalIgnoreCase))
                return width > height && string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase);
            return width < height && (string.IsNullOrEmpty(orientation) || string.Equals(orientation, "portrait", StringComparison.OrdinalIgnoreCase));
        }

        private static List<DocumentSection> DocumentSections(XDocument document)
        {
            XElement body = document.Root?.Element(W + "body");
            var sections = new List<DocumentSection>();
            var content = new List<XElement>();
            foreach (XElement element in body?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (element.Name == W + "sectPr")
                {
                    sections.Add(new DocumentSection(content, element));
                    content = new List<XElement>();
                    continue;
                }
                content.Add(element);
                XElement properties = element.Name == W + "p" ? element.Element(W + "pPr")?.Element(W + "sectPr") : null;
                if (properties != null)
                {
                    sections.Add(new DocumentSection(content, properties));
                    content = new List<XElement>();
                }
            }
            return sections;
        }

        private static List<SmartArtReference> FindSmartArts(PackageSnapshot package, string anchor)
        {
            XDocument document = package.Xml("word/document.xml");
            var result = new List<SmartArtReference>();
            foreach (XElement data in document.Descendants(A + "graphicData")
                .Where(value => string.Equals((string)value.Attribute("uri"), Dgm.NamespaceName, StringComparison.Ordinal)))
            {
                XElement paragraph = data.Ancestors(W + "p").FirstOrDefault();
                if (paragraph == null || ParagraphTextOutsideCitations(paragraph) != anchor) continue;
                XElement container = data.Ancestors().FirstOrDefault(value => value.Name == Wp + "anchor" || value.Name == Wp + "inline");
                string relationshipId = (string)data.Element(Dgm + "relIds")?.Attribute(R + "dm");
                string part = package.RelatedPart("word/document.xml", relationshipId);
                if (container != null && !string.IsNullOrWhiteSpace(part)) result.Add(new SmartArtReference(container, part));
            }
            return result;
        }

        private static string ParagraphTextOutsideCitations(XElement paragraph)
        {
            return string.Concat(paragraph.Descendants(W + "t").Where(text => !text.Ancestors(W + "del").Any() &&
                !text.Ancestors(W + "sdt").Any(value => value.Element(W + "sdtPr")?.Element(W + "citation") != null))
                .Select(text => text.Value)).TrimEnd();
        }

        private static bool ParagraphUsesFormatting(XElement paragraph, string alignment, string style)
        {
            XElement properties = paragraph.Element(W + "pPr");
            if (!string.Equals((string)properties?.Element(W + "jc")?.Attribute(W + "val"), alignment, StringComparison.Ordinal) ||
                !string.Equals((string)properties?.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val"), style, StringComparison.Ordinal))
                return false;
            List<XElement> visibleRuns = paragraph.Descendants(W + "r").Where(run => run.Descendants(W + "t")
                .Any(text => text.Value.Length > 0 && !text.Ancestors(W + "sdt").Any(value => value.Element(W + "sdtPr")?.Element(W + "citation") != null))).ToList();
            return visibleRuns.Count > 0 && visibleRuns.All(run => string.Equals(
                (string)run.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val"), style, StringComparison.Ordinal));
        }

        private static bool HasActiveCommentOnText(XElement paragraph, string targetText)
        {
            int targetStart = ParagraphText(paragraph).IndexOf(targetText, StringComparison.Ordinal);
            if (targetStart < 0) return false;
            int targetEnd = targetStart + targetText.Length;
            int offset = 0;
            var starts = new Dictionary<string, int>(StringComparer.Ordinal);
            var ends = new Dictionary<string, int>(StringComparer.Ordinal);
            var references = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement element in paragraph.Descendants())
            {
                if (element.Name == W + "commentRangeStart") starts[(string)element.Attribute(W + "id") ?? ""] = offset;
                else if (element.Name == W + "commentRangeEnd") ends[(string)element.Attribute(W + "id") ?? ""] = offset;
                else if (element.Name == W + "commentReference") references.Add((string)element.Attribute(W + "id") ?? "");
                else if (element.Name == W + "t" && !element.Ancestors(W + "del").Any()) offset += element.Value.Length;
            }
            return starts.Any(pair => references.Contains(pair.Key) && ends.ContainsKey(pair.Key) && pair.Value < targetEnd && ends[pair.Key] > targetStart);
        }

        private static bool TableRowsEqual(XElement table, string[][] expected)
        {
            List<XElement> rows = table.Elements(W + "tr").ToList();
            if (rows.Count != expected.Length) return false;
            for (int row = 0; row < rows.Count; row++)
            {
                List<string> cells = rows[row].Elements(W + "tc").Select(c => VisibleText(c).TrimEnd()).ToList();
                if (cells.Count != expected[row].Length || cells.Where((cell, column) => !string.Equals(cell, expected[row][column], StringComparison.Ordinal)).Any()) return false;
            }
            return true;
        }

        private static string ParagraphText(XElement paragraph) { return VisibleText(paragraph).TrimEnd(); }

        private static string ParagraphTextOutsideTextBoxes(XElement paragraph)
        {
            return string.Concat(paragraph.Descendants(W + "t")
                .Where(t => !t.Ancestors(W + "txbxContent").Any() && !t.Ancestors(W + "del").Any()).Select(t => t.Value)).TrimEnd();
        }

        private static int CountOccurrences(string value, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            for (int index = 0; (index = value.IndexOf(text, index, StringComparison.Ordinal)) >= 0; index += text.Length) count++;
            return count;
        }

        private static string NormalizeFieldCode(string value)
        {
            return string.Join(" ", (value ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool NullableTwipsEquals(XElement spacing, string attribute, JArray expected, int index)
        {
            if (expected == null || index >= expected.Count) throw new InvalidDataException("Invalid spacing metadata: " + attribute);
            string actual = (string)spacing?.Attribute(W + attribute);
            return expected[index].Type == JTokenType.Null ? actual == null : actual == expected[index].ToString();
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

        private static JArray RequiredArray(TaskDefinition task, string name)
        {
            JToken token;
            JArray array = task.Extra != null && task.Extra.TryGetValue(name, out token) ? token as JArray : null;
            if (array == null || array.Count == 0) throw new InvalidDataException("Missing grading metadata: " + name);
            return array;
        }

        private static string[][] RequiredStringMatrix(TaskDefinition task, string name)
        {
            JArray rows = RequiredArray(task, name);
            var result = new List<string[]>();
            foreach (JToken rowToken in rows)
            {
                JArray row = rowToken as JArray;
                if (row == null || row.Count == 0 || row.Any(value => value.Type != JTokenType.String))
                    throw new InvalidDataException("Invalid grading metadata matrix: " + name);
                result.Add(row.Values<string>().ToArray());
            }
            return result.ToArray();
        }

        private static int RequiredInt(TaskDefinition task, string name)
        {
            JToken token;
            int value;
            if (task.Extra == null || !task.Extra.TryGetValue(name, out token) || !int.TryParse(token.ToString(), out value))
                throw new InvalidDataException("Missing grading metadata: " + name);
            return value;
        }

        private static string RequiredObjectString(JObject value, string name)
        {
            string result = (string)value[name];
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("Missing grading metadata: " + name);
            return result;
        }

        private static bool OptionalBool(TaskDefinition task, string name, bool defaultValue)
        {
            JToken token;
            bool value;
            return task.Extra != null && task.Extra.TryGetValue(name, out token) && bool.TryParse(token.ToString(), out value)
                ? value : defaultValue;
        }

        private static string HashBytes(byte[] value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "");
        }

        private sealed class TextBoxCandidate
        {
            public TextBoxCandidate(XElement outerParagraph, XElement textBox, string geometry, string fillColor)
            {
                OuterParagraph = outerParagraph;
                TextBox = textBox;
                Geometry = geometry;
                FillColor = fillColor;
            }

            public XElement OuterParagraph { get; }
            public XElement TextBox { get; }
            public string Geometry { get; }
            public string FillColor { get; }
        }

        private sealed class DocumentSection
        {
            public DocumentSection(List<XElement> content, XElement properties)
            {
                Content = content;
                Properties = properties;
            }

            public List<XElement> Content { get; }
            public XElement Properties { get; }
        }

        private sealed class SmartArtReference
        {
            public SmartArtReference(XElement container, string dataPart)
            {
                Container = container;
                DataPart = dataPart;
            }

            public XElement Container { get; }
            public string DataPart { get; }
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

            public IEnumerable<string> EntryNames { get { return entries.Keys; } }

            public bool TryXml(string part, out XDocument document)
            {
                byte[] bytes;
                if (!entries.TryGetValue(Normalize(part), out bytes)) { document = null; return false; }
                try
                {
                    using (var stream = new MemoryStream(bytes, false)) document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
                    return true;
                }
                catch (System.Xml.XmlException) { document = null; return false; }
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
