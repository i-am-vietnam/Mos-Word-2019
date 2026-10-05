using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static bool CheckInsertedWindowTable(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            var headings = body.Elements(W + "p").Where(p => ParagraphText(p) == RequiredString(task, "precedingHeading")).ToList();
            var table = headings.Count == 1 ? headings[0].ElementsAfterSelf().FirstOrDefault(e => e.Name != W + "p" || !string.IsNullOrWhiteSpace(ParagraphText(e))) : null;
            if (table?.Name != W + "tbl")
            { detail = "Location: one table must immediately follow the declared heading."; return false; }
            var expected = RequiredStringMatrix(task, "expectedTableRows");
            int columns = expected[0].Length;
            var rows = table.Elements(W + "tr").ToList();
            var grid = table.Element(W + "tblGrid")?.Elements(W + "gridCol").ToList();
            if (rows.Count != expected.Length || rows.Any(r => r.Elements(W + "tc").Count() != columns) ||
                grid?.Count != columns || grid.Any(c => Twips(c, "w", -1) <= 0))
            { detail = "Dimensions: incorrect row count, column count or grid."; return false; }
            if (table.Descendants().Any(e => e.Name == W + "gridSpan" || e.Name == W + "vMerge" || e.Name == W + "hMerge"))
            { detail = "Structure: merged cells are not permitted."; return false; }
            // Only task-declared labels are required; unspecified cells are learner-editable.
            for (int i = 0; i < expected.Length; i++)
                for (int j = 0; j < columns; j++)
                    if (!string.IsNullOrEmpty(expected[i][j]) && string.Join("\n", rows[i].Elements(W + "tc").ElementAt(j).Elements(W + "p").Select(ParagraphText)) != expected[i][j])
                    { detail = "Labels: required text is missing from its declared cell."; return false; }
            var properties = new XElement(W + "tblPr");
            var direct = table.Element(W + "tblPr");
            string styleId = (string)direct?.Element(W + "tblStyle")?.Attribute(W + "val");
            foreach (var pr in StyleChain(package.Xml("word/styles.xml").Root, styleId ?? "TableNormal")
                .Select(s => s.Element(W + "tblPr")).Concat(new[] { direct }).Where(p => p != null))
                foreach (var e in pr.Elements()) { properties.Elements(e.Name).Remove(); properties.Add(new XElement(e)); }
            string layout = (string)properties.Element(W + "tblLayout")?.Attribute(W + "type");
            var width = properties.Element(W + "tblW");
            int percent = Twips(width, "w", -1);
            // Native Insert Table saves 99.9% (4995); Layout > AutoFit Window saves 100% (5000).
            // A page-sized grid alone never proves window fitting.
            if ((layout != null && layout != "autofit") || (string)width?.Attribute(W + "type") != "pct" || percent < 4995 || percent > 5000)
            { detail = "Width: require native 99.9–100% window preference and automatic layout; fixed/contents widths do not qualify."; return false; }
            foreach (var row in rows)
                for (int j = 0; j < columns; j++)
                {
                    var cellWidth = row.Elements(W + "tc").ElementAt(j).Element(W + "tcPr")?.Element(W + "tcW");
                    string type = (string)cellWidth?.Attribute(W + "type");
                    int value = Twips(cellWidth, "w", -1);
                    bool agrees = cellWidth == null || type == "auto" ||
                        (type == "pct" && Math.Abs(value - 5000.0 / columns) <= 1) ||
                        (type == "dxa" && Math.Abs(value - Twips(grid[j], "w", -1)) <= 1);
                    if (!agrees) { detail = "Width: cell preferred widths contradict the window-fitted column grid."; return false; }
                }
            detail = "The table has the required location, dimensions, labels and native AutoFit Window semantics.";
            return true;
        }

        private static bool CheckTableRowCharacterStyle(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var tables = FindTablesByHeader(package.Xml("word/document.xml"), RequiredStrings(task, "targetHeaderRow"));
            string style = RequiredString(task, "characterStyleId");
            var styles = package.Xml("word/styles.xml").Root;
            bool match = tables.Count == 1 && TableRowsEqual(tables[0], RequiredStringMatrix(task, "expectedTableRows")) &&
                styles.Elements(W + "style").Any(s => (string)s.Attribute(W + "styleId") == style && (string)s.Attribute(W + "type") == "character");
            if (match)
            {
                int rowIndex = RequiredInt(task, "rowIndex");
                var row = tables[0].Elements(W + "tr").ElementAtOrDefault(rowIndex);
                match = row != null && row.Elements(W + "tc").All(cell =>
                {
                    var runs = cell.Descendants(W + "r").Where(r => VisibleText(r).Length > 0).ToList();
                    return runs.Count > 0 && runs.All(r => (string)r.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val") == style);
                });
            }
            detail = match ? "All visible text in the declared table row uses the requested character style."
                : "The table content or character style on one or more target row characters is incorrect.";
            return match;
        }
    }
}
