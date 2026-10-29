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
        private static List<XElement> BodyTextShapes(XElement body)
        {
            // A readable Choice and its fallback describe one object. Prefer the readable native shape.
            var choices = body.Descendants(Wps + "wsp").Where(s => !s.Ancestors(Mc + "Fallback").Any() &&
                s.Element(Wps + "txbx")?.Element(W + "txbxContent") != null).ToList();
            var legacy = body.Descendants(V + "textbox").Where(box => box.Element(W + "txbxContent") != null).Select(box => box.Parent).Where(s =>
                !s.Ancestors(Mc + "AlternateContent").Any(alt => alt.Elements(Mc + "Choice").Descendants(Wps + "wsp").Any())).ToList();
            return choices.Concat(legacy).ToList();
        }

        private static bool BodyTextShapeVisible(PackageSnapshot package, XElement shape)
        {
            var drawing = shape.Ancestors().FirstOrDefault(e => e.Name == Wp + "anchor" || e.Name == Wp + "inline") ?? shape;
            string style = ((string)shape.Attribute("style") ?? "").Replace(" ", "").ToLowerInvariant();
            return DrawingRunsVisible(package, shape) && !style.Contains("visibility:hidden") && !style.Contains("display:none") &&
                !drawing.DescendantsAndSelf().Any(e => (e.Name.LocalName == "docPr" || e.Name.LocalName == "cNvPr" || e.Name.LocalName == "cNvSpPr") &&
                    new[] { "1", "true", "on" }.Contains((string)e.Attribute("hidden")));
        }

        private static XElement ShapeTextStory(XElement shape)
        { return shape.Name == Wps + "wsp" ? shape.Element(Wps + "txbx")?.Element(W + "txbxContent") : shape.Element(V + "textbox")?.Element(W + "txbxContent"); }

        private static string ShapeLogicalText(XElement shape)
        { return string.Join("\n", ShapeTextStory(shape).Elements(W + "p").Select(LogicalParagraphText)); }

        private static bool CheckBodyWordArtTextEffect(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string expected = RequiredString(task, "expectedText");
            foreach (string key in new[] { "expectedThemeColor", "expectedFillRgb", "textExtrusionHeight", "textMaterial",
                "textBevelPreset", "textBevelWidth", "textBevelHeight", "textCameraPreset", "textLightRig", "textLightDirection", "textLightRotation" })
                RequiredString(task, key);
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            var shapes = BodyTextShapes(body).Where(s => ShapeLogicalText(s) == expected).ToList();
            detail = "The original beginning title must be a unique native WordArt text box with the declared complete text effect.";
            if (shapes.Count != 1 || body.Elements(W + "p").Any(p => LogicalParagraphText(p).Contains(expected))) return false;
            var shape = shapes[0];
            if (!BodyTitlePositionMatches(body, shape) || !BodyTextShapeVisible(package, shape)) return false;
            var story = ShapeTextStory(shape);
            if (story.Descendants(W + "tbl").Any() || story.Descendants().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom")) return false;
            var nativeBody = shape.Element(Wps + "bodyPr");
            // Native WordArt is a text-effect-bearing textbox; fromWordArt is 0 in the observed Word preset.
            if (shape.Name == Wps + "wsp" && (nativeBody?.Element(A + "prstTxWarp") == null ||
                !PositiveDrawingExtent(shape.Ancestors().FirstOrDefault(e => e.Name == Wp + "anchor" || e.Name == Wp + "inline")?.Element(Wp + "extent")) ||
                !PositiveDrawingExtent(shape.Element(Wps + "spPr")?.Element(A + "xfrm")?.Element(A + "ext")))) return false;
            foreach (var paragraph in story.Elements(W + "p"))
                foreach (var run in paragraph.Descendants(W + "r").Where(r => r.Descendants(W + "t").Any(t => t.Value.Length > 0)))
                {
                    var format = EffectiveRunFormatting(package, paragraph, run);
                    if (new[] { "vanish", "webHidden", "specVanish", "outline" }.Any(format.ContainsKey) ||
                        !WordArtFillMatches(package, paragraph, run, format, task)) return false;
                    string xml;
                    XElement effect = format.TryGetValue((W14 + "props3d").ToString(), out xml) ? XElement.Parse(xml) : nativeBody?.Element(A + "sp3d");
                    if (!WordArtBevelMatches(effect, task)) return false;
                    XElement scene = format.TryGetValue((W14 + "scene3d").ToString(), out xml) ? XElement.Parse(xml) : nativeBody?.Element(A + "scene3d");
                    if (!WordArtSceneMatches(scene, task)) return false;
                    if (format.TryGetValue((W14 + "textOutline").ToString(), out xml))
                    {
                        var outline = XElement.Parse(xml);
                        if (outline.Element(W14 + "noFill") == null || ((string)outline.Attribute(W14 + "w") ?? "0") != "0") return false;
                    }
                }
            return story.Descendants(W + "t").Any(t => t.Value.Length > 0);
        }

        private static bool WordArtFillMatches(PackageSnapshot package, XElement paragraph, XElement run,
            Dictionary<string, string> format, TaskDefinition task)
        {
            string theme = RequiredString(task, "expectedThemeColor"), rgb = RequiredString(task, "expectedFillRgb");
            string xml;
            if (format.TryGetValue((W14 + "textFill").ToString(), out xml))
            {
                var fill = XElement.Parse(xml); var solid = fill.Element(W14 + "solidFill");
                var color = solid?.Element(W14 + "schemeClr");
                return fill.Elements().Count() == 1 && solid != null && solid.Elements().Count() == 1 && (string)color?.Attribute(W14 + "val") == theme && !color.Elements().Any() &&
                    ThemeDrawingRgb(package, theme) == rgb;
            }
            var effectiveColor = EffectiveRunColorElement(package, paragraph, run);
            string actual;
            return (string)effectiveColor?.Attribute(W + "themeColor") == theme && effectiveColor.Attribute(W + "themeTint") == null &&
                effectiveColor.Attribute(W + "themeShade") == null && format.TryGetValue("color", out actual) && actual == rgb && ThemeDrawingRgb(package, theme) == rgb;
        }

        private static string ThemeDrawingRgb(PackageSnapshot package, string theme)
        {
            var color = package.Xml("word/theme/theme1.xml").Descendants(A + "clrScheme").Single().Element(A + theme)?.Elements().FirstOrDefault();
            return ((string)color?.Attribute("val") ?? (string)color?.Attribute("lastClr") ?? "").ToUpperInvariant();
        }

        private static bool WordArtBevelMatches(XElement effect, TaskDefinition task)
        {
            if (effect == null) return false;
            XNamespace n = effect.Name.Namespace;
            Func<string, string> attr = name => (string)effect.Attribute(n == W14 ? n + name : XName.Get(name));
            var bevel = effect.Element(n + "bevelT");
            Func<string, string> bevelAttr = name => (string)bevel?.Attribute(n == W14 ? n + name : XName.Get(name));
            return attr("extrusionH") == RequiredString(task, "textExtrusionHeight") && (attr("contourW") ?? "0") == "0" &&
                attr("prstMaterial") == RequiredString(task, "textMaterial") && bevel != null &&
                (bevelAttr("prst") ?? "circle") == RequiredString(task, "textBevelPreset") &&
                bevelAttr("w") == RequiredString(task, "textBevelWidth") && bevelAttr("h") == RequiredString(task, "textBevelHeight") &&
                !effect.Elements(n + "bevelB").Any();
        }

        private static bool WordArtSceneMatches(XElement scene, TaskDefinition task)
        {
            if (scene == null) return false;
            XNamespace n = scene.Name.Namespace;
            Func<XElement, string, string> value = (e, name) => (string)e?.Attribute(n == W14 ? n + name : XName.Get(name));
            var camera = scene.Element(n + "camera"); var rig = scene.Element(n + "lightRig"); var rotation = rig?.Element(n + "rot");
            return value(camera, "prst") == RequiredString(task, "textCameraPreset") && !camera.Elements(n + "rot").Any() &&
                value(rig, "rig") == RequiredString(task, "textLightRig") && value(rig, "dir") == RequiredString(task, "textLightDirection") &&
                (value(rotation, "lat") ?? "0") == "0" && (value(rotation, "lon") ?? "0") == "0" &&
                (value(rotation, "rev") ?? "0") == RequiredString(task, "textLightRotation");
        }

        private static bool BodyTitlePositionMatches(XElement body, XElement shape)
        {
            var children = body.Elements().ToList();
            var tables = children.Where(e => e.Name == W + "tbl").ToList();
            return tables.Count == 1 && !shape.Ancestors(W + "tbl").Any() && !shape.Ancestors(W + "txbxContent").Any() &&
                children.IndexOf(BodyChild(shape)) >= 0 && children.IndexOf(BodyChild(shape)) < children.IndexOf(tables[0]);
        }

        private static bool CheckDeclaredBodyContentStates(PackageSnapshot package, TaskDefinition task)
        {
            if (!task.Extra.ContainsKey("preservedTableVariants")) return true;
            var variants = RequiredArray(task, "preservedTableVariants");
            if (FindTableMatchingVariants(package, variants) == null) return false;
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            if (body.Descendants(W + "tbl").Count() != 1) return false;
            string title = RequiredString(task, "preservedTitleText"), note = RequiredString(task, "preservedNoteText");
            var shapes = BodyTextShapes(body);
            if (shapes.Any(s => ShapeLogicalText(s) != title && ShapeLogicalText(s) != RequiredString(task, "allowedShapeText")) ||
                shapes.Count(s => ShapeLogicalText(s) == title) > 1 || shapes.Count(s => ShapeLogicalText(s) == RequiredString(task, "allowedShapeText")) > 1) return false;
            var main = body.Elements(W + "p").Where(p => !string.IsNullOrWhiteSpace(LogicalParagraphText(p))).ToList();
            var titleShapes = shapes.Where(s => ShapeLogicalText(s) == title).ToList();
            if (titleShapes.Count == 0)
            {
                if (!main.Select(LogicalParagraphText).SequenceEqual(new[] { title, note }) ||
                    XNode.CompareDocumentOrder(main[0], body.Elements(W + "tbl").Single()) >= 0 || !BodyTitleTextVisible(package, main[0])) return false;
            }
            else if (!main.Select(LogicalParagraphText).SequenceEqual(new[] { note }) || !BodyTitlePositionMatches(body, titleShapes[0]) ||
                !BodyTextShapeVisible(package, titleShapes[0]) || !BodyTitleTextVisible(package, ShapeTextStory(titleShapes[0]))) return false;
            return XNode.CompareDocumentOrder(body.Elements(W + "tbl").Single(), main.Last()) < 0 && BodyTitleTextVisible(package, main.Last()) &&
                body.Elements().All(e => e.Name == W + "p" || e.Name == W + "tbl" || e.Name == W + "sectPr") &&
                !body.Descendants(W + "object").Any() && !body.Descendants(Pic + "pic").Any();
        }

        private static bool BodyTitleTextVisible(PackageSnapshot package, XElement text)
        {
            return !text.Descendants().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom") &&
                text.Descendants(W + "r").Where(r => r.Descendants(W + "t").Any(t => t.Value.Length > 0)).All(r =>
                    !new[] { "vanish", "webHidden", "specVanish" }.Any(EffectiveRunFormatting(package, r.Ancestors(W + "p").First(), r).ContainsKey));
        }
    }
}
