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
        private static bool CheckTableVariants(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            bool match = FindTableMatchingVariants(package, RequiredArray(task, "expectedTableVariants")) != null;
            detail = match ? "The complete table matches a declared text state with its original rows, cells and paragraphs."
                : "The table text, row order or cell structure differs from every declared state.";
            return match;
        }

        private static XElement FindTableMatchingVariants(PackageSnapshot package, JArray expected)
        {
            var variants = TableVariantMatrices(expected);
            var body = package.Xml("word/document.xml").Root?.Element(W + "body");
            if (body == null) return null;
            var matches = body.Descendants(W + "tbl").Where(t => !t.Ancestors(W + "tbl").Any() &&
                !t.Ancestors(W + "txbxContent").Any() && variants.Any(rows => TableVariantMatrixMatches(package, t, rows))).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static List<string[][]> TableVariantMatrices(JArray expected)
        {
            if (expected == null || expected.Count == 0 || expected.Any(matrix => !(matrix is JArray) || !matrix.HasValues ||
                ((JArray)matrix).Any(row => !(row is JArray) || !row.HasValues || ((JArray)row).Any(cell => cell.Type != JTokenType.String)) ||
                ((JArray)matrix).Cast<JArray>().Select(row => row.Count).Distinct().Count() != 1))
                throw new InvalidDataException("Table variants must contain nonempty rectangular matrices of cell strings.");
            return expected.Cast<JArray>().Select(matrix => matrix.Cast<JArray>().Select(row => row.Values<string>().ToArray()).ToArray()).ToList();
        }

        private static bool TableVariantMatrixMatches(PackageSnapshot package, XElement table, string[][] expected)
        {
            if (!TableRowsEqual(table, expected) || table.Ancestors().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom") ||
                table.Descendants().Any(e => e.Name == W + "tbl" || e.Name == W + "vMerge" || e.Name == W + "hMerge" ||
                    e.Name == W + "del" || e.Name == W + "moveFrom" || e.Name == W + "delText" || e.Name == W + "fldChar" ||
                    e.Name == W + "fldSimple" || e.Name == W + "instrText" || e.Name == W + "drawing" || e.Name == W + "pict" || e.Name == W + "object")) return false;
            var grids = table.Elements(W + "tblGrid").ToList();
            if (grids.Count != 1 || grids[0].Elements(W + "gridCol").Count() != expected[0].Length ||
                table.Elements().Any(e => e.Name != W + "tblPr" && e.Name != W + "tblGrid" && e.Name != W + "tr")) return false;
            var rows = table.Elements(W + "tr").ToList();
            for (int r = 0; r < rows.Count; r++)
            {
                if (rows[r].Elements().Any(e => e.Name != W + "trPr" && e.Name != W + "tc") ||
                    rows[r].Element(W + "trPr")?.Elements().Any(e => (e.Name == W + "gridBefore" || e.Name == W + "gridAfter") &&
                        (string)e.Attribute(W + "val") != "0") == true) return false;
                var cells = rows[r].Elements(W + "tc").ToList();
                for (int c = 0; c < cells.Count; c++)
                {
                    var paragraphs = cells[c].Elements(W + "p").ToList();
                    if (paragraphs.Count != 1 || cells[c].Elements().Any(e => e.Name != W + "tcPr" && e.Name != W + "p") ||
                        cells[c].Element(W + "tcPr")?.Elements(W + "gridSpan").Any(e => (string)e.Attribute(W + "val") != "1") == true ||
                        LogicalParagraphText(paragraphs[0]) != expected[r][c]) return false;
                    foreach (var run in paragraphs[0].Descendants(W + "r").Where(run => run.Descendants(W + "t").Any(t => t.Value.Length > 0)))
                        if (new[] { "vanish", "webHidden", "specVanish" }.Any(EffectiveRunFormatting(package, paragraphs[0], run).ContainsKey)) return false;
                }
            }
            return true;
        }

        private static bool CheckTableRowTextFormatting(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            JArray variants = RequiredArray(task, "expectedTableVariants");
            var matrices = TableVariantMatrices(variants);
            JArray target = RequiredArray(task, "targetRowVariants");
            if (target.Any(row => !(row is JArray) || !row.HasValues || ((JArray)row).Any(cell => cell.Type != JTokenType.String) ||
                !matrices.Any(matrix => matrix[0].Length == ((JArray)row).Count)))
                throw new InvalidDataException("Target row variants must contain arrays of cell strings.");
            JToken token;
            var expected = task.Extra.TryGetValue("expectedFormatting", out token) ? token as JObject : null;
            if (expected == null || !expected.HasValues || expected.Properties().Any(p => p.Value.Type != JTokenType.String))
                throw new InvalidDataException("Expected row text formatting is missing or invalid.");
            var table = FindTableMatchingVariants(package, variants);
            var rows = table?.Elements(W + "tr").Where(row => target.Cast<JArray>().Any(variant =>
                row.Elements(W + "tc").Select(cell => LogicalParagraphText(cell.Element(W + "p"))).SequenceEqual(variant.Values<string>()))).ToList();
            detail = "Every character of the declared row must retain its text and have the requested effective formatting.";
            if (rows == null || rows.Count != 1) return false;
            bool additional = OptionalBool(task, "allowAdditionalFormatting", false);
            foreach (var paragraph in rows[0].Elements(W + "tc").Elements(W + "p"))
                foreach (var run in paragraph.Descendants(W + "r").Where(run => run.Descendants(W + "t").Any(t => t.Value.Length > 0)))
                {
                    var actual = EffectiveRunFormatting(package, paragraph, run);
                    if ((!additional && actual.Count != expected.Count) || expected.Properties().Any(p =>
                        !actual.ContainsKey(p.Name) || actual[p.Name] != (string)p.Value)) return false;
                }
            return true;
        }
    }
}
