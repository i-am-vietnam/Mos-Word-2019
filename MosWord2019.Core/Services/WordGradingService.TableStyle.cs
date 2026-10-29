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
        // Unlike the older table contracts, these declarations preserve every logical character.
        private static XElement FindDeclaredTableExact(PackageSnapshot package, TaskDefinition task)
        {
            var expected = RequiredStringMatrix(task, "expectedTableRows");
            var header = RequiredArray(task, "targetHeaderRow");
            if (expected.Any(row => row.Length != expected[0].Length) ||
                header.Any(cell => cell.Type != JTokenType.String) || !header.Values<string>().SequenceEqual(expected[0]))
                throw new InvalidDataException("The declared table header and rectangular rows must agree.");
            var tables = package.Xml("word/document.xml").Root.Element(W + "body").Descendants(W + "tbl")
                .Where(table => !table.Ancestors(W + "tbl").Any() && !table.Ancestors(W + "txbxContent").Any() &&
                    table.Elements(W + "tr").FirstOrDefault()?.Elements(W + "tc")
                        .Select(cell => string.Join("\n", cell.Elements(W + "p").Select(LogicalParagraphText)))
                        .SequenceEqual(expected[0]) == true).ToList();
            return tables.Count == 1 && DeclaredTableMatchesExact(package, tables[0], expected) ? tables[0] : null;
        }

        private static bool DeclaredTableMatchesExact(PackageSnapshot package, XElement table, string[][] expected)
        {
            if (table == null || expected == null || expected.Length == 0 || expected[0].Length == 0 ||
                expected.Any(row => row.Length != expected[0].Length) ||
                table.Ancestors().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom") ||
                table.Descendants().Any(e => new[] { "tbl", "vMerge", "hMerge", "del", "moveFrom", "delText", "fldChar", "fldSimple", "instrText", "drawing", "pict", "object", "sym", "noBreakHyphen", "softHyphen", "ptab", "footnoteReference", "endnoteReference" }.Any(name => e.Name == W + name) ||
                    e.Name == Mc + "AlternateContent" || e.Name.LocalName == "oMath" || e.Name.LocalName == "oMathPara")) return false;
            var grids = table.Elements(W + "tblGrid").ToList();
            var rows = table.Elements(W + "tr").ToList();
            if (grids.Count != 1 || grids[0].Elements(W + "gridCol").Count() != expected[0].Length || rows.Count != expected.Length ||
                table.Elements().Any(e => e.Name != W + "tblPr" && e.Name != W + "tblGrid" && e.Name != W + "tr")) return false;
            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r].Elements(W + "tc").ToList();
                if (cells.Count != expected[r].Length || rows[r].Elements().Any(e => e.Name != W + "trPr" && e.Name != W + "tc") ||
                    rows[r].Element(W + "trPr")?.Elements().Any(e => (e.Name == W + "gridBefore" || e.Name == W + "gridAfter") && (string)e.Attribute(W + "val") != "0") == true) return false;
                for (int c = 0; c < cells.Count; c++)
                {
                    var paragraphs = cells[c].Elements(W + "p").ToList();
                    if (paragraphs.Count != 1 || LogicalParagraphText(paragraphs[0]) != expected[r][c] ||
                        cells[c].Elements().Any(e => e.Name != W + "tcPr" && e.Name != W + "p") ||
                        cells[c].Element(W + "tcPr")?.Elements(W + "gridSpan").Any(e => (string)e.Attribute(W + "val") != "1") == true) return false;
                    foreach (var run in paragraphs[0].Descendants(W + "r").Where(run => run.Descendants(W + "t").Any(t => t.Value.Length > 0)))
                        if (new[] { "vanish", "webHidden", "specVanish" }.Any(EffectiveRunFormatting(package, paragraphs[0], run).ContainsKey)) return false;
                }
            }
            return true;
        }

        private static bool CheckTableStyleApplied(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            JToken token;
            var expected = task.Extra.TryGetValue("expectedTableStyleProperties", out token) ? token as JObject : null;
            var colors = task.Extra.TryGetValue("expectedThemeColors", out token) ? token as JObject : null;
            if (expected == null || !expected.HasValues || expected.Properties().Any(p => p.Value.Type != JTokenType.String) ||
                colors == null || !colors.HasValues || colors.Properties().Any(p => p.Value.Type != JTokenType.String))
                throw new InvalidDataException("The native table-style properties and theme colors must be declared.");
            var table = FindDeclaredTableExact(package, task);
            detail = "The complete declared table must use the requested native table-style semantics and theme.";
            if (table == null) return false;
            string id = (string)table.Element(W + "tblPr")?.Element(W + "tblStyle")?.Attribute(W + "val");
            if (string.IsNullOrEmpty(id)) return false;
            var chain = StyleChain(package.Xml("word/styles.xml").Root, id).ToList();
            if (chain.Count == 0 || chain[0].Element(W + "basedOn") != null || (string)chain.Last().Attribute(W + "styleId") != id ||
                chain.Any(style => (string)style.Attribute(W + "type") != "table")) return false;
            var actual = TableStyleProperties(package, chain);
            return actual.Count == expected.Count && expected.Properties().All(p => actual.ContainsKey(p.Name) && actual[p.Name] == (string)p.Value) &&
                colors.Properties().All(p => ResolveColor(package, new XElement(W + "color", new XAttribute(W + "themeColor", p.Name)), "val", "themeColor", "themeTint", "themeShade") == ((string)p.Value).ToUpperInvariant());
        }

        private static Dictionary<string, string> TableStyleProperties(PackageSnapshot package, IEnumerable<XElement> chain)
        {
            var properties = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var style in chain)
            {
                foreach (var group in style.Elements().Where(e => new[] { "pPr", "rPr", "tblPr", "trPr", "tcPr" }.Any(name => e.Name == W + name)))
                    MergeTableStyleProperties(group, "wholeTable/" + group.Name.LocalName, properties);
                foreach (var condition in style.Elements(W + "tblStylePr"))
                    foreach (var group in condition.Elements())
                        MergeTableStyleProperties(group, (string)condition.Attribute(W + "type") + "/" + (group.Name.Namespace == W ? group.Name.LocalName : group.Name.ToString()), properties);
            }
            return properties.ToDictionary(pair => pair.Key, pair => TableStylePropertyValue(package, pair.Value), StringComparer.Ordinal);
        }

        private static void MergeTableStyleProperties(XElement group, string path, Dictionary<string, XElement> properties)
        {
            foreach (var property in group.Elements())
            {
                if (property.Name.LocalName.EndsWith("Change", StringComparison.Ordinal)) continue;
                string key = path + "/" + (property.Name.Namespace == W ? property.Name.LocalName : property.Name.ToString());
                if (property.HasElements) { MergeTableStyleProperties(property, key, properties); continue; }
                XElement inherited;
                var value = new XElement(property);
                // Style run toggles follow the same inheritance rule as EffectiveRunFormatting.
                if (property.Name.Namespace == W && ToggleProperties.Contains(property.Name.LocalName))
                {
                    if (!On(property)) continue;
                    if (properties.TryGetValue(key, out inherited) && On(inherited)) { properties.Remove(key); continue; }
                    value.SetAttributeValue(W + "val", "1");
                }
                if (properties.TryGetValue(key, out inherited) && new[] { "spacing", "ind", "rFonts", "lang" }.Contains(property.Name.LocalName))
                    foreach (var attr in inherited.Attributes().Where(a => !a.IsNamespaceDeclaration && value.Attribute(a.Name) == null))
                        value.SetAttributeValue(attr.Name, attr.Value);
                properties[key] = value;
            }
        }

        private static string TableStylePropertyValue(PackageSnapshot package, XElement property)
        {
            var attributes = property.Attributes().Where(a => !a.IsNamespaceDeclaration).ToDictionary(a => a.Name.Namespace == W ? a.Name.LocalName : "foreign:" + a.Name, a => a.Value, StringComparer.Ordinal);
            if (ToggleProperties.Contains(property.Name.LocalName)) attributes["val"] = On(property) ? "1" : "0";
            if (attributes.ContainsKey("themeColor"))
            {
                attributes.Remove(property.Name == W + "color" ? "val" : "color");
                attributes["resolvedColor"] = ResolveColor(package, property, property.Name == W + "color" ? "val" : "color", "themeColor", "themeTint", "themeShade");
            }
            if (attributes.ContainsKey("themeFill"))
            {
                attributes.Remove("fill");
                attributes["resolvedFill"] = ResolveColor(package, property, "fill", "themeFill", "themeFillTint", "themeFillShade");
            }
            return string.Join("|", attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value));
        }
    }
}
