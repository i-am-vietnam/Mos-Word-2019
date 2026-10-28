using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static bool CheckTextWatermark(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string text = RequiredString(task, "expectedText"), font = RequiredString(task, "expectedFont");
            detail = "Every active section header must contain the requested visible text watermark and font.";
            var sections = DocumentSections(package.Xml("word/document.xml"));
            var inherited = new Dictionary<string, string>(StringComparer.Ordinal);
            XDocument settings;
            bool even = package.TryXml("word/settings.xml", out settings) && IsOn(settings.Root?.Element(W + "evenAndOddHeaders"));
            for (int index = 0; index < sections.Count; index++)
            {
                XElement properties = sections[index].Properties;
                var references = properties.Elements(W + "headerReference").ToList();
                if (references.GroupBy(r => (string)r.Attribute(W + "type") ?? "default").Any(g => g.Count() != 1)) return false;
                foreach (var reference in references)
                    inherited[(string)reference.Attribute(W + "type") ?? "default"] = (string)reference.Attribute(R + "id");
                var active = new List<string> { "default" };
                if (even) active.Add("even");
                // A continuous section defers its first-page header, as in the existing header reader.
                if (IsOn(properties.Element(W + "titlePg")) &&
                    (index == 0 || (string)properties.Element(W + "type")?.Attribute(W + "val") != "continuous")) active.Add("first");
                foreach (string type in active)
                {
                    string id;
                    if (!inherited.TryGetValue(type, out id)) return false;
                    string part = package.RelatedPart("word/document.xml", id);
                    XDocument header;
                    if (part == null || !package.TryXml(part, out header) || header.Root?.Name != W + "hdr" ||
                        !header.Descendants(V + "shape").Any(shape => TextWatermarkMatches(package, header.Root, shape, text, font))) return false;
                }
            }
            return sections.Count > 0;
        }

        private static bool TextWatermarkMatches(PackageSnapshot package, XElement header, XElement shape, string text, string font)
        {
            XElement pict = shape.Ancestors(W + "pict").FirstOrDefault();
            if (pict == null || shape.Ancestors().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom" || e.Name == W + "txbxContent") ||
                shape.Descendants(V + "imagedata").Any() || shape.Descendants(W + "txbxContent").Any()) return false;
            string type = (string)shape.Attribute("type");
            if (type == null || !type.StartsWith("#", StringComparison.Ordinal)) return false;
            var definitions = header.Descendants(V + "shapetype").Where(t => (string)t.Attribute("id") == type.Substring(1)).ToList();
            var local = pict.Elements(V + "shapetype").Where(t => (string)t.Attribute("id") == type.Substring(1)).ToList();
            if (local.Count > 0) definitions = local;
            XNamespace office = "urn:schemas-microsoft-com:office:office";
            if (definitions.Count != 1 || (string)definitions[0].Attribute(office + "spt") != "136") return false;
            XElement definition = definitions[0];
            var layout = WatermarkStyle(definition);
            foreach (var property in WatermarkStyle(shape)) layout[property.Key] = property.Value;
            string position, order;
            long zIndex;
            if (!layout.TryGetValue("position", out position) || !string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase) ||
                !layout.TryGetValue("z-index", out order) || !long.TryParse(order, NumberStyles.Integer, CultureInfo.InvariantCulture, out zIndex) || zIndex >= 0) return false;
            foreach (string axis in new[] { "horizontal", "vertical" })
            {
                string alignment, relative;
                if (!layout.TryGetValue("mso-position-" + axis, out alignment) || !string.Equals(alignment, "center", StringComparison.OrdinalIgnoreCase) ||
                    !layout.TryGetValue("mso-position-" + axis + "-relative", out relative) ||
                    !new[] { "margin", "page" }.Contains(relative.ToLowerInvariant())) return false;
            }
            var paths = shape.Elements(V + "textpath").ToList();
            if (paths.Count != 1 || (string)paths[0].Attribute("string") != text) return false;
            XElement path = paths[0], inheritedPath = definition.Element(V + "textpath");
            string enabled = (string)path.Attribute("on") ?? (string)inheritedPath?.Attribute("on");
            string allowed = (string)shape.Element(V + "path")?.Attribute("textpathok") ?? (string)definition.Element(V + "path")?.Attribute("textpathok");
            if (!WatermarkOn(enabled, false) || !WatermarkOn(allowed, false)) return false;
            string family;
            var textStyle = WatermarkStyle(inheritedPath);
            foreach (var property in WatermarkStyle(path)) textStyle[property.Key] = property.Value;
            if (!textStyle.TryGetValue("font-family", out family) ||
                !string.Equals(family.Trim().Trim('"', '\''), font, StringComparison.OrdinalIgnoreCase)) return false;
            var run = shape.Ancestors(W + "r").FirstOrDefault();
            var paragraph = run?.Ancestors(W + "p").FirstOrDefault();
            if (run == null || paragraph == null) return false;
            var formatting = EffectiveRunFormatting(package, paragraph, run);
            if (new[] { "vanish", "webHidden", "specVanish" }.Any(formatting.ContainsKey)) return false;
            foreach (var element in shape.AncestorsAndSelf().Concat(new[] { definition, inheritedPath, path }).Where(e => e != null))
            {
                var style = WatermarkStyle(element); string value;
                if ((style.TryGetValue("visibility", out value) && new[] { "hidden", "collapse" }.Contains(value.ToLowerInvariant())) ||
                    (style.TryGetValue("display", out value) && string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)) ||
                    new[] { "width", "height", "opacity" }.Any(key => style.TryGetValue(key, out value) && WatermarkZero(value))) return false;
            }
            bool filled = WatermarkOn((string)shape.Attribute("filled") ?? (string)definition.Attribute("filled"), true);
            bool stroked = WatermarkOn((string)shape.Attribute("stroked") ?? (string)definition.Attribute("stroked"), true);
            XElement fill = shape.Element(V + "fill") ?? definition.Element(V + "fill");
            XElement stroke = shape.Element(V + "stroke") ?? definition.Element(V + "stroke");
            filled &= WatermarkOn((string)fill?.Attribute("on"), true) && !WatermarkZero((string)fill?.Attribute("opacity"));
            stroked &= WatermarkOn((string)stroke?.Attribute("on"), true) && !WatermarkZero((string)stroke?.Attribute("opacity"));
            return filled || stroked;
        }

        private static Dictionary<string, string> WatermarkStyle(XElement element)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in ((string)element?.Attribute("style") ?? "").Split(';'))
            {
                int colon = item.IndexOf(':');
                if (colon > 0) result[item.Substring(0, colon).Trim()] = item.Substring(colon + 1).Trim();
            }
            return result;
        }

        private static bool WatermarkOn(string value, bool fallback)
        { return value == null ? fallback : new[] { "t", "true", "1", "on" }.Contains(value.ToLowerInvariant()); }

        private static bool WatermarkZero(string value)
        {
            if (value == null) return false;
            string number = value.Trim().TrimEnd('%');
            foreach (string suffix in new[] { "pt", "px", "in", "cm", "mm", "f" })
                if (number.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { number = number.Substring(0, number.Length - suffix.Length); break; }
            double parsed;
            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed <= 0;
        }
    }
}
