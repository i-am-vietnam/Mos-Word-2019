using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static bool CheckCoverPageAssembly(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            bool match = CheckDeclaredCoverAssembly(package, task, true);
            detail = match ? "The native cover assembly is at the beginning and the specified address control is removed."
                : "The cover assembly, property controls or complete address-control removal differs.";
            return match;
        }

        private static bool CheckDeclaredCoverAssembly(PackageSnapshot package, TaskDefinition task, bool requireDeletedAddress)
        {
            JToken token;
            var expected = task.Extra.TryGetValue("expectedCover", out token) ? token as JObject : null;
            if (expected == null) throw new InvalidDataException("Missing cover assembly metadata.");
            string gallery = RequiredObjectString(expected, "gallery"), band = RequiredObjectString(expected, "bandTheme"), background = RequiredObjectString(expected, "titleBackgroundTheme");
            var frames = expected["shapeFrames"] as JObject;
            if (frames == null || new[] { "upper", "lower", "title" }.Any(role => !(frames[role] is JArray) || ((JArray)frames[role]).Count != 4 ||
                ((JArray)frames[role]).Any(v => v.Type != JTokenType.Float && v.Type != JTokenType.Integer)))
                throw new InvalidDataException("Invalid native cover frame metadata.");
            var paths = new Dictionary<string, string[]>();
            foreach (string name in new[] { "authorBinding", "companyBinding", "titleBinding", "addressBinding" })
            {
                var path = expected[name] as JArray;
                if (path == null || path.Count != 2 || path.Any(v => v.Type != JTokenType.String || !((string)v).StartsWith("{", StringComparison.Ordinal)))
                    throw new InvalidDataException("Invalid cover binding metadata.");
                paths[name] = path.Values<string>().ToArray();
            }
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            var covers = body.Elements(W + "sdt").ToList();
            if (covers.Count != 1 || body.Elements().TakeWhile(e => e != covers[0]).Any(e => e.Name != W + "p" || !IsBlankTableBoundary(e))) return false;
            var cover = covers[0];
            var part = cover.Element(W + "sdtPr")?.Element(W + "docPartObj");
            if ((string)part?.Element(W + "docPartGallery")?.Attribute(W + "val") != gallery || part.Element(W + "docPartUnique") == null) return false;
            var content = cover.Element(W + "sdtContent");
            var anchors = content?.Descendants(Wp + "anchor").Where(e => !e.Ancestors(Mc + "Fallback").Any()).ToList();
            if (anchors == null || anchors.Count != 1) return false;
            var anchor = anchors[0];
            var group = anchor.Element(A + "graphic")?.Element(A + "graphicData")?.Element(Wpg + "wgp");
            if (group == null || !PositiveDrawingExtent(anchor.Element(Wp + "extent")) || !DrawingRunsVisible(package, group) ||
                (string)anchor.Attribute("behindDoc") != "1" || anchor.Element(Wp + "wrapNone") == null ||
                new[] { "1", "true", "on" }.Contains((string)anchor.Attribute("simplePos")) ||
                new[] { "positionH", "positionV" }.Any(axis => (string)anchor.Element(Wp + axis)?.Attribute("relativeFrom") != "page" || (string)anchor.Element(Wp + axis)?.Element(Wp + "align") != "center") ||
                anchor.Descendants().Any(e => (e.Name.LocalName == "docPr" || e.Name.LocalName == "cNvPr") && new[] { "1", "true", "on" }.Contains((string)e.Attribute("hidden")))) return false;
            var shapes = group.Elements(Wps + "wsp").ToList();
            if (shapes.Count != 3 || group.Descendants(Wps + "wsp").Count() != 3) return false;
            var groupTransform = group.Element(Wpg + "grpSpPr")?.Element(A + "xfrm");
            if (groupTransform == null || !CoverTransformUnrotated(groupTransform) || !PositiveDrawingExtent(groupTransform.Element(A + "ext")) ||
                !PositiveDrawingExtent(groupTransform.Element(A + "chExt"))) return false;
            double groupWidth = (double)groupTransform.Element(A + "ext").Attribute("cx"), groupHeight = (double)groupTransform.Element(A + "ext").Attribute("cy");
            var page = DocumentSections(body.Document).First().Properties.Element(W + "pgSz");
            if (groupWidth < Twips(page, "w", 0) * 635.0 / 2 || groupHeight < Twips(page, "h", 0) * 635.0 / 2 ||
                Math.Abs(groupWidth - (double)anchor.Element(Wp + "extent").Attribute("cx")) > 12700 ||
                Math.Abs(groupHeight - (double)anchor.Element(Wp + "extent").Attribute("cy")) > 12700) return false;
            foreach (var shape in shapes)
                if ((string)shape.Element(Wps + "spPr")?.Element(A + "prstGeom")?.Attribute("prst") != "rect" ||
                    !CoverTransformUnrotated(shape.Element(Wps + "spPr")?.Element(A + "xfrm")) ||
                    !PositiveDrawingExtent(shape.Element(Wps + "spPr")?.Element(A + "xfrm")?.Element(A + "ext"))) return false;
            var textShapes = shapes.Where(s => s.Element(Wps + "txbx") != null).ToList();
            var bands = shapes.Where(s => (string)s.Element(Wps + "spPr")?.Element(A + "solidFill")?.Element(A + "schemeClr")?.Attribute("val") == band).ToList();
            if (textShapes.Count != 2 || bands.Count != 2 || bands.Count(s => s.Element(Wps + "txbx") == null) != 1 ||
                bands.Any(s => s.Element(Wps + "spPr").Element(A + "solidFill").Elements().Single().HasElements || s.Element(Wps + "spPr").Element(A + "ln")?.Element(A + "noFill") == null)) return false;
            var lower = bands.Single(s => s.Element(Wps + "txbx") != null);
            var title = textShapes.SingleOrDefault(s => s != lower);
            if (title == null || (string)title.Element(Wps + "spPr")?.Element(A + "solidFill")?.Element(A + "schemeClr")?.Attribute("val") != background ||
                title.Element(Wps + "spPr").Element(A + "solidFill").Elements().Single().HasElements) return false;
            var upper = bands.Single(s => s != lower);
            if (!CoverFrameMatches(upper, groupTransform, (JArray)frames["upper"]) || !CoverFrameMatches(lower, groupTransform, (JArray)frames["lower"]) ||
                !CoverFrameMatches(title, groupTransform, (JArray)frames["title"])) return false;
            long upperY, titleY, lowerY, upperH, titleH;
            if (!CoverCoordinate(upper, "off", "y", out upperY) || !CoverCoordinate(title, "off", "y", out titleY) || !CoverCoordinate(lower, "off", "y", out lowerY) ||
                !CoverCoordinate(upper, "ext", "cy", out upperH) || !CoverCoordinate(title, "ext", "cy", out titleH) || upperY < 0 || titleY < upperY + upperH || lowerY < titleY + titleH) return false;
            var controls = group.Descendants(W + "sdt").ToList();
            var address = controls.Where(c => CoverBindingPath(c).SequenceEqual(paths["addressBinding"])).ToList();
            if (address.Count > 1 || requireDeletedAddress && address.Count != 0 || controls.Count != 3 + address.Count) return false;
            foreach (string name in new[] { "authorBinding", "companyBinding", "titleBinding" })
            {
                var found = controls.Where(c => CoverBindingPath(c).SequenceEqual(paths[name])).ToList();
                if (found.Count != 1 || found[0].Element(W + "sdtPr")?.Element(W + "text") == null ||
                    !found[0].Ancestors(Wps + "wsp").Contains(name == "titleBinding" ? title : lower)) return false;
                var value = found[0].Element(W + "sdtContent");
                string text = string.Concat(value.Descendants(W + "t").Select(t => t.Value));
                string property = (string)package.Xml(name == "companyBinding" ? "docProps/app.xml" : "docProps/core.xml").Root.Element(XName.Get(paths[name][1]));
                if (property != null ? text != property : !IsOn(found[0].Element(W + "sdtPr").Element(W + "showingPlcHdr"))) return false;
            }
            if (group.Descendants().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom" || e.Name == W + "object" || e.Name == Pic + "pic") ||
                group.Descendants(W + "r").Where(r => r.Descendants(W + "t").Any()).Any(r => new[] { "vanish", "webHidden", "specVanish" }.Any(EffectiveRunFormatting(package, r.Ancestors(W + "p").First(), r).ContainsKey))) return false;
            if (group.Descendants(W + "t").Where(t => !t.Ancestors(W + "sdt").Any(controls.Contains)).Any(t => t.Value.Any(c => !char.IsWhiteSpace(c)))) return false;
            if (content.Descendants(W + "tbl").Any() || content.Descendants(W + "t").Where(t => !t.Ancestors(Mc + "Fallback").Any() && !t.Ancestors(Wpg + "wgp").Contains(group)).Any(t => !string.IsNullOrWhiteSpace(t.Value))) return false;
            return content.Descendants(W + "br").Any(b => (string)b.Attribute(W + "type") == "page");
        }

        private static bool CoverCoordinate(XElement shape, string element, string attribute, out long value)
        { return long.TryParse((string)shape.Element(Wps + "spPr")?.Element(A + "xfrm")?.Element(A + element)?.Attribute(attribute), out value); }

        private static bool CoverTransformUnrotated(XElement transform)
        {
            return transform != null && ((string)transform.Attribute("rot") ?? "0") == "0" &&
                !new[] { "flipH", "flipV" }.Any(a => new[] { "1", "true", "on" }.Contains((string)transform.Attribute(a)));
        }

        private static bool CoverFrameMatches(XElement shape, XElement group, JArray expected)
        {
            var frame = shape.Element(Wps + "spPr").Element(A + "xfrm");
            var offset = group.Element(A + "chOff"); var extent = group.Element(A + "chExt");
            double x, y, width, height;
            if (!double.TryParse((string)offset?.Attribute("x"), out x) || !double.TryParse((string)offset?.Attribute("y"), out y) ||
                !double.TryParse((string)extent?.Attribute("cx"), out width) || !double.TryParse((string)extent?.Attribute("cy"), out height) || width <= 0 || height <= 0) return false;
            long left, top, w, h;
            if (!CoverCoordinate(shape, "off", "x", out left) || !CoverCoordinate(shape, "off", "y", out top) ||
                !CoverCoordinate(shape, "ext", "cx", out w) || !CoverCoordinate(shape, "ext", "cy", out h)) return false;
            var actual = new[] { (left - x) / width, (top - y) / height, w / width, h / height };
            return actual.Select((v, i) => Math.Abs(v - (double)expected[i]) <= 0.01).All(v => v);
        }

        private static string[] CoverBindingPath(XElement control)
        {
            var binding = control.Element(W + "sdtPr")?.Element(W + "dataBinding");
            var mappings = Regex.Matches((string)binding?.Attribute(W + "prefixMappings") ?? "", "xmlns:(?<p>[A-Za-z_][\\w.-]*)\\s*=\\s*['\"](?<u>[^'\"]+)['\"]")
                .Cast<Match>().ToDictionary(m => m.Groups["p"].Value, m => m.Groups["u"].Value);
            var segments = ((string)binding?.Attribute(W + "xpath") ?? "").Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();
            foreach (string segment in segments)
            {
                var match = Regex.Match(segment, "^(?<p>[A-Za-z_][\\w.-]*):(?<n>[A-Za-z_][\\w.-]*)(?:\\[1\\])?$");
                string ns;
                if (!match.Success || !mappings.TryGetValue(match.Groups["p"].Value, out ns)) return new string[0];
                result.Add((XName.Get(match.Groups["n"].Value, ns)).ToString());
            }
            return result.ToArray();
        }
    }
}
