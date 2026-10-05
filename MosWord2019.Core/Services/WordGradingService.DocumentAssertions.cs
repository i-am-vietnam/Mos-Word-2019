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
        private static string EffectiveParagraphAlignment(PackageSnapshot package, XElement paragraph)
        {
            XElement styles = package.Xml("word/styles.xml").Root;
            string alignment = (string)styles.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val") ?? "left";
            string styleId = (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ??
                (string)styles.Elements(W + "style").FirstOrDefault(s => (string)s.Attribute(W + "type") == "paragraph" && (string)s.Attribute(W + "default") == "1")?.Attribute(W + "styleId") ?? "Normal";
            foreach (var style in StyleChain(styles, styleId))
                alignment = (string)style.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val") ?? alignment;
            return (string)paragraph.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val") ?? alignment;
        }

        private static bool CheckDocumentMargins(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var sections = package.Xml("word/document.xml").Descendants(W + "sectPr")
                .Where(s => !s.Ancestors(W + "sectPrChange").Any()).ToList();
            var settings = package.Xml("word/settings.xml").Root;
            bool match = sections.Count > 0 && !IsOn(settings.Element(W + "mirrorMargins")) &&
                !IsOn(settings.Element(W + "bookFoldPrinting")) && !IsOn(settings.Element(W + "bookFoldRevPrinting")) &&
                sections.All(s => new[] { "top", "bottom", "left", "right" }.All(edge =>
                    Twips(s.Element(W + "pgMar"), edge, -1) == RequiredInt(task, edge + "Twips")));
            detail = match ? "All active sections have the requested four document margins."
                : "One or more margins or the whole-document page layout are incorrect.";
            return match; // Header/footer distance and gutter are deliberately outside this task.
        }

        private static bool CheckTableCellSpacing(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var tables = FindTablesByHeader(package.Xml("word/document.xml"), RequiredStrings(task, "targetHeaderRow"));
            int expected = RequiredInt(task, "expectedSpacingTwips");
            bool match = tables.Count == 1 && expected > 0;
            if (match)
            {
                XElement table = tables[0];
                var spacing = table.Element(W + "tblPr")?.Element(W + "tblCellSpacing");
                match = (string)spacing?.Attribute(W + "type") == "dxa" && Twips(spacing, "w", -1) == expected &&
                    table.Elements(W + "tr").All(row =>
                    {
                        var rowSpacing = row.Element(W + "trPr")?.Element(W + "tblCellSpacing");
                        return rowSpacing == null || ((string)rowSpacing.Attribute(W + "type") == "dxa" && Twips(rowSpacing, "w", -1) == expected);
                    }) && TableRowsEqual(table, RequiredArray(task, "expectedRows").Select(row => row.Values<string>().ToArray()).ToArray());
            }
            detail = match ? "The unchanged target table has the verified enabled cell spacing."
                : "The target table content or effective cell spacing is incorrect; cell margins do not substitute for spacing.";
            return match;
        }

        private static bool CheckPictureBorderColor(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            XElement drawing = FindPictureDrawing(package, RequiredStrings(task, "targetImageSha256s"));
            XNamespace pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
            XElement line = drawing?.Descendants(pic + "spPr").Elements(A + "ln").SingleOrDefault();
            XElement scheme = line?.Element(A + "solidFill")?.Element(A + "schemeClr");
            string themeName = RequiredString(task, "expectedSchemeColor");
            XElement theme = package.Xml("word/theme/theme1.xml").Descendants(A + "clrScheme").Single().Element(A + (themeName == "tx2" ? "dk2" : themeName == "tx1" ? "dk1" : themeName == "bg1" ? "lt1" : themeName == "bg2" ? "lt2" : themeName))?.Elements().FirstOrDefault();
            string themeRgb = (string)theme?.Attribute("lastClr") ?? (string)theme?.Attribute("val");
            bool match = drawing != null && line != null && PictureGeometryMatches(drawing, task) &&
                ParagraphText(drawing.Ancestors(W + "p").FirstOrDefault()) == RequiredString(task, "anchorParagraph") &&
                (!OptionalBool(task, "requireOriginalLineStyle", true) || DrawingInteger(line, "w", 9525) == RequiredInt(task, "expectedWidthEmu")) && // Word default 0.75 pt when omitted.
                (!OptionalBool(task, "requireOriginalLineStyle", true) || ((string)line.Element(A + "prstDash")?.Attribute("val") ?? "solid") == RequiredString(task, "expectedDash")) &&
                (!OptionalBool(task, "requireOriginalLineStyle", true) || (line.Element(A + "custDash") == null && ((string)line.Attribute("cmpd") ?? "sng") == "sng")) &&
                (string)scheme?.Attribute("val") == themeName &&
                string.Equals(themeRgb, RequiredString(task, "expectedThemeRgb"), StringComparison.OrdinalIgnoreCase) &&
                scheme.Elements().Count() == 1 && DrawingInteger(scheme.Element(A + "lumMod"), "val", -1) == RequiredInt(task, "expectedLumMod");
            // DrawingML attributes are unqualified, unlike WordprocessingML Twips attributes.
            match = match && (!OptionalBool(task, "requireOriginalLineStyle", true) || line.Elements().All(e => e.Name == A + "solidFill" || e.Name == A + "prstDash" || e.Name == A + "round" || e.Name == A + "bevel" || e.Name == A + "miter"));
            detail = match ? "The source picture has the verified border-color semantics and task-declared line properties."
                : "The target picture or its task-declared border properties are incorrect.";
            return match;
        }

        private static int DrawingInteger(XElement element, string attribute, int fallback)
        {
            int value;
            return int.TryParse((string)element?.Attribute(attribute), out value) ? value : fallback;
        }

        private static bool CheckTrackedChangesDisposition(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            XElement body = package.Xml("word/document.xml").Root.Element(W + "body");
            string[] revisionElements = { "ins", "del", "moveFrom", "moveTo", "moveFromRangeStart", "moveFromRangeEnd", "moveToRangeStart", "moveToRangeEnd", "cellIns", "cellDel", "cellMerge", "numberingChange" };
            bool match = !body.Descendants().Any(e => e.Name.Namespace == W &&
                (revisionElements.Contains(e.Name.LocalName) || e.Name.LocalName.EndsWith("PrChange", StringComparison.Ordinal)));
            var generated = new HashSet<XElement>(BodyFields(body).Where(f => f.Code.ToString().TrimStart().StartsWith("TOC ", StringComparison.OrdinalIgnoreCase)).SelectMany(f => f.ResultParagraphs));
            var paragraphs = body.Descendants(W + "p").Where(p => !p.Ancestors(W + "tbl").Any() &&
                !p.Ancestors(W + "txbxContent").Any() && !generated.Contains(p)).Select(ParagraphTextOutsideTextBoxes).Where(s => s.Length > 0).ToArray();
            match &= paragraphs.SequenceEqual(RequiredStrings(task, "expectedBodyParagraphs"));
            JToken token;
            if (task.Extra.TryGetValue("protectedTables", out token))
            {
                match &= body.Descendants(W + "tbl").Count() == ((JArray)token).Count;
                foreach (JObject table in (JArray)token)
                {
                    var candidates = FindTablesByHeader(package.Xml("word/document.xml"), table["header"].Values<string>().ToArray());
                    match &= candidates.Count == 1 && TableRowsEqual(candidates[0], ((JArray)table["rows"]).Select(row => row.Values<string>().ToArray()).ToArray());
                }
            }
            if (task.Extra.TryGetValue("protectedComments", out token))
                foreach (JObject target in (JArray)token)
                {
                    var candidates = body.Descendants(W + "p").Where(p => ParagraphText(p) == (string)target["paragraph"]).ToList();
                    XDocument comments;
                    var ids = candidates.Count == 1 ? ActiveCommentIds(candidates[0], (string)target["paragraph"]) : new List<string>();
                    match &= ids.Count == 1 && package.TryXml("word/comments.xml", out comments) &&
                        comments.Root.Elements(W + "comment").Any(c => (string)c.Attribute(W + "id") == ids[0] && VisibleText(c) == (string)target["text"]);
                }
            if (task.Extra.TryGetValue("rejectedFormattingRanges", out token))
                foreach (JObject range in (JArray)token)
                {
                    var formattingTask = new TaskDefinition { Extra = range.Properties().ToDictionary(p => p.Name, p => p.Value) };
                    string formattingDetail;
                    match &= CheckTextRangeFormatting(package, formattingTask, out formattingDetail);
                }
            if (task.Extra.TryGetValue("rejectedMargins", out token))
            {
                var marginsTask = new TaskDefinition { Extra = ((JObject)token).Properties().ToDictionary(p => p.Name, p => p.Value) };
                string marginsDetail;
                match &= CheckDocumentMargins(package, marginsTask, out marginsDetail);
            }
            detail = match ? "Revision dispositions preserve accepted insertions, remove accepted deletions and retain required original formatting/content."
                : "Pending revisions, incorrect insertion/deletion disposition, changed content or accepted formatting/layout changes remain.";
            return match;
        }
    }
}
