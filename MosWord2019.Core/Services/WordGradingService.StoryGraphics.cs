using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

        private static bool CheckBodyContentBeforeSmartArt(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            var children = body.Elements().ToList();
            var preceding = children.Where(p => p.Name == W + "p" && LogicalParagraphText(p) == RequiredString(task, "precedingParagraph")).ToList();
            var diagrams = AllSmartArts(package);
            detail = "The complete imported body paragraphs must occur once between the unchanged preceding paragraph and original SmartArt.";
            if (preceding.Count != 1 || diagrams.Count != 1) return false;
            var diagram = diagrams[0];
            var ids = diagram.Container.Descendants(Dgm + "relIds").ToList();
            if (ids.Count != 1) return false;
            string layout = package.RelatedPart("word/document.xml", (string)ids[0].Attribute(R + "lo"));
            if (layout == null || (string)package.Xml(layout).Root.Attribute("uniqueId") != RequiredString(task, "followingSmartArtLayoutId")) return false;
            var references = body.Descendants(Dgm + "relIds").Where(e => !e.Ancestors(Mc + "Fallback").Any()).ToList();
            if (references.Count != 1) return false;
            var following = BodyChild(references[0]);
            int start = children.IndexOf(preceding[0]), end = children.IndexOf(following);
            if (end <= start) return false;
            var interval = children.Skip(start + 1).Take(end - start - 1).Where(e => !IsBlankTableBoundary(e)).ToList();
            string[] expected = RequiredStrings(task, "expectedParagraphs");
            return expected.Length > 0 && interval.All(p => p.Name == W + "p" && !p.Descendants().Any(e =>
                e.Name == W + "drawing" || e.Name == W + "pict" || e.Name == W + "object" || e.Name == W + "fldChar" || e.Name == W + "fldSimple")) &&
                interval.All(p => p.Descendants(W + "r").Where(r => r.Descendants(W + "t").Any()).All(r =>
                    !new[] { "vanish", "webHidden", "specVanish" }.Any(EffectiveRunFormatting(package, p, r).ContainsKey))) &&
                interval.Select(LogicalParagraphText).SequenceEqual(expected) &&
                expected.All(t => body.Descendants(W + "p").Count(p => LogicalParagraphText(p) == t) == 1);
        }

        // A readable DrawingML Choice is authoritative; its VML fallback is the same object, not a second sidebar.
        private static XElement FindDeclaredTextBox(PackageSnapshot package, TaskDefinition task)
        {
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            string heading = RequiredString(task, "textBoxHeading"), text = RequiredString(task, "textBoxParagraph");
            var groups = body.Descendants(Wpg + "wgp").Where(g => !g.Ancestors(Mc + "Fallback").Any() &&
                g.Descendants(W + "p").Any(p => LogicalParagraphText(p) == heading) &&
                g.Descendants(W + "p").Any(p => LogicalParagraphText(p) == text)).ToList();
            if (groups.Count == 1)
            {
                var group = groups[0];
                var content = group.Descendants(W + "p").Select(LogicalParagraphText).Where(t => t.Trim().Length > 0).ToList();
                if (content.Count != 2 || content.Count(t => t == heading) != 1 || content.Count(t => t == text) != 1) return null;
                var boxes = group.Descendants(W + "txbxContent").Where(b => b.Elements(W + "p").Any(p => LogicalParagraphText(p) == text)).ToList();
                var extent = group.Ancestors(Wp + "anchor").FirstOrDefault()?.Element(Wp + "extent");
                return group.Elements(Wps + "wsp").Count() == RequiredInt(task, "textBoxShapeCount") &&
                    group.Descendants(A + "solidFill").Elements(A + "srgbClr").Any(c =>
                        string.Equals((string)c.Attribute("val"), RequiredString(task, "textBoxFillRgb"), StringComparison.OrdinalIgnoreCase)) &&
                    boxes.Count == 1 && PositiveDrawingExtent(extent) && DrawingRunsVisible(package, group) ? boxes[0] : null;
            }
            if (groups.Count != 0) return null;
            // Older Word may retain only the legacy representation. Do not fall back around a damaged readable Choice.
            if (body.Descendants(Wpg + "wgp").Any()) return null;
            var legacy = body.Descendants(V + "group").Where(g => g.Descendants(W + "p").Any(p => LogicalParagraphText(p) == heading) &&
                g.Descendants(W + "p").Any(p => LogicalParagraphText(p) == text)).ToList();
            if (legacy.Count != 1) return null;
            var shapes = legacy[0].Elements().Where(s => s.Name == V + "shape" || s.Name == V + "rect").ToList();
            if (shapes.Count != RequiredInt(task, "textBoxShapeCount") || !DrawingRunsVisible(package, legacy[0])) return null;
            var legacyText = legacy[0].Descendants(W + "p").Select(LogicalParagraphText).Where(t => t.Trim().Length > 0).ToList();
            var legacyBoxes = legacy[0].Descendants(W + "txbxContent").Where(b => b.Elements(W + "p").Any(p => LogicalParagraphText(p) == text)).ToList();
            return legacyText.Count == 2 && legacyText.Count(t => t == heading) == 1 && legacyText.Count(t => t == text) == 1 &&
                legacyBoxes.Count == 1 ? legacyBoxes[0] : null;
        }

        private static bool CheckTextBoxPictureBevel(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var box = FindDeclaredTextBox(package, task);
            detail = "The supplied embedded photograph must follow the unchanged text inside the original sidebar and carry the required picture bevel.";
            if (box == null) return false;
            var paragraphs = box.Elements(W + "p").ToList();
            var text = paragraphs.Single(p => LogicalParagraphText(p) == RequiredString(task, "textBoxParagraph"));
            var pictures = box.Descendants(Pic + "pic").Where(p => box.Ancestors(Mc + "Fallback").Any() || !p.Ancestors(Mc + "Fallback").Any()).ToList();
            if (pictures.Count != 1) return false;
            var picture = pictures[0];
            if (picture.Ancestors().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom")) return false;
            var run = picture.Ancestors(W + "r").FirstOrDefault();
            var paragraph = run?.Ancestors(W + "p").FirstOrDefault();
            if (paragraph == null || !picture.Ancestors().Any(e => e.Name == Wp + "inline" || e.Name == Wp + "anchor")) return false;
            var container = picture.Ancestors().First(e => e.Name == Wp + "inline" || e.Name == Wp + "anchor");
            if (!PositiveDrawingExtent(container.Element(Wp + "extent")) ||
                !PositiveDrawingExtent(picture.Element(Pic + "spPr")?.Element(A + "xfrm")?.Element(A + "ext"))) return false;
            var formatting = EffectiveRunFormatting(package, paragraph, run);
            if (new[] { "vanish", "webHidden", "specVanish" }.Any(formatting.ContainsKey)) return false;
            // Require text-story order, including when Word keeps the picture at the end of the text paragraph.
            var tokens = box.Descendants().Where(e => e.Name == W + "t" || e.Name == Pic + "pic").ToList();
            if (text.Descendants(W + "t").Any(t => tokens.IndexOf(t) > tokens.IndexOf(picture))) return false;
            var blips = picture.Element(Pic + "blipFill")?.Elements(A + "blip").ToList();
            if (blips == null || blips.Count != 1 || blips[0].Attribute(R + "link") != null) return false;
            if (blips[0].Elements(A + "alphaModFix").Any(e => DrawingInteger(e, "amt", 100000) <= 0) ||
                blips[0].Elements(A + "alphaRepl").Any(e => DrawingInteger(e, "a", 100000) <= 0)) return false;
            string id = (string)blips[0].Attribute(R + "embed");
            var relationships = package.Xml("word/_rels/document.xml.rels").Root.Elements(Rel + "Relationship").Where(r => (string)r.Attribute("Id") == id).ToList();
            if (relationships.Count != 1 || (string)relationships[0].Attribute("Type") != R.NamespaceName + "/image" ||
                (string)relationships[0].Attribute("TargetMode") == "External") return false;
            string part = package.RelatedPart("word/document.xml", id);
            if (part == null || !RequiredStrings(task, "targetImageSha256s").Contains(package.Hash(part), StringComparer.OrdinalIgnoreCase)) return false;
            var bevel = picture.Element(Pic + "spPr")?.Element(A + "sp3d")?.Element(A + "bevelT");
            return bevel != null && (string)bevel.Attribute("prst") == RequiredString(task, "expectedBevelPreset") &&
                DrawingInteger(bevel, "w", 76200) > 0 && DrawingInteger(bevel, "h", 76200) > 0;
        }

        private static bool PositiveDrawingExtent(XElement extent)
        { return DrawingInteger(extent, "cx", 0) > 0 && DrawingInteger(extent, "cy", 0) > 0; }

        private static bool DrawingRunsVisible(PackageSnapshot package, XElement drawing)
        {
            return !drawing.Ancestors().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom") &&
                drawing.Ancestors(W + "r").All(r => !new[] { "vanish", "webHidden", "specVanish" }
                    .Any(EffectiveRunFormatting(package, r.Ancestors(W + "p").First(), r).ContainsKey));
        }

        private static bool PreservedStoryGraphicsMatch(PackageSnapshot package, TaskDefinition task)
        {
            if (task.Extra.ContainsKey("textBoxHeading") && FindDeclaredTextBox(package, task) == null) return false;
            if (!task.Extra.ContainsKey("preservedConnectorAnchor")) return true;
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            var anchors = body.Elements(W + "p").Where(p => LogicalParagraphText(p) == RequiredString(task, "preservedConnectorAnchor")).ToList();
            if (anchors.Count != 1) return false;
            var lines = anchors[0].Descendants(Wps + "wsp").Where(s => s.Element(Wps + "cNvCnPr") != null &&
                (string)s.Element(Wps + "spPr")?.Element(A + "prstGeom")?.Attribute("prst") == "line").ToList();
            if (lines.Count != 1 || !lines[0].Ancestors(Wp + "anchor").Any() || !DrawingRunsVisible(package, lines[0])) return false;
            var properties = lines[0].Element(Wps + "spPr");
            var line = properties?.Element(A + "ln");
            var colors = line?.Element(A + "solidFill")?.Elements().ToList() ?? new List<XElement>();
            if (colors.Count > 1) return false;
            var color = colors.FirstOrDefault();
            string theme = RequiredString(task, "preservedConnectorThemeColor");
            return line?.Element(A + "noFill") == null &&
                (color == null || !color.Elements().Any() && (color.Name == A + "schemeClr" && (string)color.Attribute("val") == theme ||
                    color.Name == A + "srgbClr" && (string)color.Attribute("val") == RequiredString(task, "preservedConnectorRgb"))) &&
                DrawingInteger(properties?.Element(A + "xfrm")?.Element(A + "ext"), "cx", 0) == RequiredInt(task, "preservedConnectorWidthEmu") &&
                (string)lines[0].Element(Wps + "style")?.Element(A + "lnRef")?.Element(A + "schemeClr")?.Attribute("val") == theme;
        }
    }
}
