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
            bool match = table?.Name == W + "tbl";
            if (match)
            {
                var properties = table.Element(W + "tblPr");
                var width = properties?.Element(W + "tblW");
                string layout = (string)properties?.Element(W + "tblLayout")?.Attribute(W + "type");
                var rows = RequiredStringMatrix(task, "expectedTableRows");
                int columns = rows[0].Length;
                var grid = table.Element(W + "tblGrid")?.Elements(W + "gridCol").ToList();
                match = TableRowsEqual(table, rows) && grid?.Count == columns && grid.All(c => Twips(c, "w", -1) > 0) &&
                    !table.Descendants().Any(e => e.Name == W + "gridSpan" || e.Name == W + "vMerge" || e.Name == W + "hMerge") &&
                    (layout == null || layout == "autofit") && (string)width?.Attribute(W + "type") == "pct" && Twips(width, "w", -1) == 5000 &&
                    table.Elements(W + "tr").SelectMany(r => r.Elements(W + "tc")).All(c =>
                    {
                        var cellWidth = c.Element(W + "tcPr")?.Element(W + "tcW");
                        return (string)cellWidth?.Attribute(W + "type") == "pct" && Twips(cellWidth, "w", -1) == 5000 / columns;
                    });
            }
            detail = match ? "The unchanged inserted table has the declared dimensions and native AutoFit Window below its heading."
                : "The inserted table location, content, dimensions or AutoFit Window state is incorrect.";
            return match;
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
