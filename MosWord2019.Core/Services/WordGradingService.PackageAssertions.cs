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
        private static bool CheckPageBorder(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            JToken confirmed;
            if (task.Extra == null || !task.Extra.TryGetValue("colorExpectationConfirmed", out confirmed) || confirmed.Type != JTokenType.Boolean || !(bool)confirmed)
                throw new InvalidDataException("The task's color wording conflicts with the supplied theme. Expected page-border color needs confirmation.");
            string color = RequiredString(task, "expectedColor");
            string style = RequiredString(task, "expectedStyle");
            int size = RequiredInt(task, "expectedSizeEighthPoints");
            var sections = package.Xml("word/document.xml").Descendants(W + "sectPr")
                .Where(s => !s.Ancestors(W + "sectPrChange").Any()).ToList();
            bool match = sections.Count > 0 && sections.All(s =>
            {
                var borders = s.Element(W + "pgBorders");
                string display = (string)borders?.Attribute(W + "display") ?? "allPages";
                return borders != null && display == "allPages" && new[] { "top", "left", "bottom", "right" }.All(edge =>
                {
                    var b = borders.Element(W + edge);
                    return b != null && (string)b.Attribute(W + "val") == style && Twips(b, "sz", -1) == size &&
                        (!task.Extra.ContainsKey("expectedThemeColor") || (string)b.Attribute(W + "themeColor") == (string)task.Extra["expectedThemeColor"]) &&
                        !new[] { "1", "true", "on" }.Contains((string)b.Attribute(W + "shadow")) &&
                        !new[] { "1", "true", "on" }.Contains((string)b.Attribute(W + "frame")) &&
                        ResolveColor(package, b, "color", "themeColor", "themeTint", "themeShade") == color.ToUpperInvariant();
                });
            });
            detail = match ? "All four page borders have the required style, width and effective color on every page of every section."
                : "The page border style, width, color or whole-document scope is incorrect.";
            return match;
        }

        private static List<XElement> MainParagraphs(PackageSnapshot package)
        {
            return package.Xml("word/document.xml").Root.Element(W + "body").Descendants(W + "p")
                .Where(p => !p.Ancestors(W + "txbxContent").Any()).ToList();
        }

        private static bool CheckBodyReplacement(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            if (task.Extra.ContainsKey("replacementLocations"))
                return CheckLocatedReplacement(package, task, out detail);
            string oldText = RequiredString(task, "oldText"), replacement = RequiredString(task, "newText");
            string expected = RequiredString(task, "expectedParagraph");
            var paragraphs = MainParagraphs(package);
            string all = string.Join("\n", paragraphs.Select(ParagraphText));
            bool match = paragraphs.Count(p => ParagraphText(p) == expected) == 1 && CountOccurrences(all, oldText) == 0 &&
                CountOccurrences(all, replacement) == RequiredInt(task, "expectedReplacementCount");
            if (task.Extra.ContainsKey("expectedReplacementParagraphs"))
                match &= RequiredStrings(task, "expectedReplacementParagraphs").All(text => paragraphs.Count(p => ParagraphText(p) == text) == 1);
            if (task.Extra.ContainsKey("expectedBodyParagraphs"))
            {
                var body = paragraphs.FirstOrDefault()?.Ancestors(W + "body").FirstOrDefault();
                if (body == null) match = false;
                else
                {
                    // Generated TOC results may legitimately change in another task. Compare the
                    // remaining visible main-body paragraphs exactly, including their order/count.
                    var generated = new HashSet<XElement>(BodyFields(body)
                        .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Code.ToString(), @"^\s*TOC\b",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase)).SelectMany(f => f.ResultParagraphs));
                    match &= paragraphs.Where(p => !generated.Contains(p)).Select(ParagraphTextOutsideTextBoxes)
                        .Where(t => !string.IsNullOrWhiteSpace(t)).SequenceEqual(RequiredStrings(task, "expectedBodyParagraphs"));
                }
            }
            detail = match ? "Every target occurrence is replaced; the target paragraph is otherwise unchanged."
                : "A source occurrence remains, the replacement count is wrong, or surrounding text changed.";
            return match;
        }

        private static bool CheckCustomBullet(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var paragraphs = MainParagraphs(package);
            string[] expected = RequiredStrings(task, "targetParagraphs");
            JToken scope;
            bool scoped = task.Extra.TryGetValue("sectionHeading", out scope);
            int anchor = scoped ? paragraphs.FindIndex(p => ParagraphTextOutsideTextBoxes(p) == (string)scope)
                : paragraphs.FindIndex(p => ParagraphText(p) == RequiredString(task, "precedingParagraph"));
            string glyph = RequiredString(task, "expectedGlyph"), font = RequiredString(task, "expectedBulletFont");
            JToken alternatives;
            var variants = task.Extra.TryGetValue("paragraphTextAlternatives", out alternatives) ? alternatives as JObject : null;
            var targets = new List<XElement>();
            bool match = anchor >= 0;
            var candidates = scoped ? paragraphs.Skip(anchor + 1).TakeWhile(p => ParagraphTextOutsideTextBoxes(p) != RequiredString(task, "followingHeading")).ToList()
                : paragraphs.Skip(anchor + 1).Take(expected.Length).ToList();
            Func<XElement, string> text = p => scoped ? BulletParagraphText(p, task) : ParagraphText(p);
            for (int i = 0; match && i < expected.Length; i++)
            {
                Func<XElement, bool> accepts = p => text(p) == expected[i] ||
                    (variants?[expected[i]] is JArray && ((JArray)variants[expected[i]]).Values<string>().Contains(text(p)));
                var found = scoped ? candidates.Where(accepts).ToList() : candidates.Skip(i).Take(1).Where(accepts).ToList();
                match = found.Count == 1 && (targets.Count == 0 || candidates.IndexOf(found[0]) > candidates.IndexOf(targets.Last()));
                if (match) targets.Add(found[0]);
            }
            for (int i = 0; match && i < expected.Length; i++)
            {
                XElement level = EffectiveNumberingLevel(package, targets[i], new HashSet<string>());
                XElement fonts = level?.Element(W + "rPr")?.Element(W + "rFonts");
                string ascii = (string)fonts?.Attribute(W + "ascii"), highAnsi = (string)fonts?.Attribute(W + "hAnsi");
                match = (string)level?.Element(W + "numFmt")?.Attribute(W + "val") == "bullet" &&
                    (string)level?.Element(W + "lvlText")?.Attribute(W + "val") == glyph &&
                    level?.Element(W + "lvlPicBulletId") == null &&
                    (ascii == font || highAnsi == font) && (ascii == null || ascii == font) && (highAnsi == null || highAnsi == font);
            }
            detail = match ? "The unchanged target paragraphs use real custom bullets with the required glyph and font."
                : "One or more target paragraphs lack the required real bullet glyph/font or their content/order changed.";
            return match;
        }

        private static string BulletParagraphText(XElement paragraph, TaskDefinition task)
        {
            // Optional symbols are narrowly declared, including their exact logical insertion position.
            string value = ParagraphTextOutsideTextBoxes(paragraph);
            JToken token;
            if (!task.Extra.TryGetValue("optionalInlineSymbol", out token)) return value;
            string after = (string)token["afterText"], font = (string)token["font"];
            int code = (int)token["code"];
            var symbols = paragraph.Descendants(W + "sym").ToList();
            if (symbols.Count > 1) return "\0";
            foreach (var symbol in symbols)
            {
                int parsed;
                if ((string)symbol.Attribute(W + "font") != font ||
                    !int.TryParse((string)symbol.Attribute(W + "char"), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out parsed) || parsed % 4096 != code ||
                    string.Concat(paragraph.Descendants(W + "t").Where(t => XNode.DocumentOrderComparer.Compare(t, symbol) < 0).Select(t => t.Value)) != after)
                    return "\0";
            }
            // Native Word also serializes legacy-font symbols as a private-use text run.
            string privateUse = char.ConvertFromUtf32(0xF000 + code);
            int position = value.IndexOf(privateUse, StringComparison.Ordinal);
            if (position >= 0)
            {
                var runs = paragraph.Descendants(W + "r").Where(r => VisibleText(r).Contains(privateUse)).ToList();
                if (symbols.Count != 0 || value.LastIndexOf(privateUse, StringComparison.Ordinal) != position || position != after.Length || runs.Count != 1 ||
                    (string)runs[0].Element(W + "rPr")?.Element(W + "rFonts")?.Attribute(W + "ascii") != font) return "\0";
                value = value.Remove(position, privateUse.Length);
            }
            return value;
        }

        private static XElement EffectiveNumberingLevel(PackageSnapshot package, XElement paragraph, HashSet<string> visited)
        {
            XElement styles = package.Xml("word/styles.xml").Root;
            var chain = StyleChain(styles, (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "Normal");
            XElement numPr = new XElement(W + "numPr");
            foreach (XElement properties in chain.Select(s => s.Element(W + "pPr")?.Element(W + "numPr"))
                .Concat(new[] { paragraph.Element(W + "pPr")?.Element(W + "numPr") }).Where(p => p != null))
                foreach (var e in properties.Elements()) { numPr.Elements(e.Name).Remove(); numPr.Add(new XElement(e)); }
            string numId = (string)numPr.Element(W + "numId")?.Attribute(W + "val");
            string ilvl = (string)numPr.Element(W + "ilvl")?.Attribute(W + "val") ?? "0";
            if (numId == null || numId == "0") return null;
            return NumberingLevel(package.Xml("word/numbering.xml").Root, styles, numId, ilvl, visited);
        }

        private static XElement NumberingLevel(XElement numbering, XElement styles, string numId, string ilvl, HashSet<string> visited)
        {
            if (numId == null || numId == "0" || !visited.Add(numId + "/" + ilvl)) return null;
            var num = numbering.Elements(W + "num").SingleOrDefault(n => (string)n.Attribute(W + "numId") == numId);
            var over = num?.Elements(W + "lvlOverride").SingleOrDefault(n => (string)n.Attribute(W + "ilvl") == ilvl)?.Element(W + "lvl");
            string abstractId = (string)num?.Element(W + "abstractNumId")?.Attribute(W + "val");
            var definition = numbering.Elements(W + "abstractNum").SingleOrDefault(n => (string)n.Attribute(W + "abstractNumId") == abstractId);
            string link = (string)definition?.Element(W + "numStyleLink")?.Attribute(W + "val");
            if (link != null)
            {
                var style = StyleChain(styles, link).Select(s => s.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId"))
                    .LastOrDefault(e => e != null);
                var linked = NumberingLevel(numbering, styles, (string)style?.Attribute(W + "val"), ilvl, visited);
                return MergeNumberingLevel(linked, over);
            }
            return MergeNumberingLevel(definition?.Elements(W + "lvl").SingleOrDefault(n => (string)n.Attribute(W + "ilvl") == ilvl), over);
        }

        private static XElement MergeNumberingLevel(XElement basis, XElement over)
        {
            if (over == null) return basis;
            var result = basis == null ? new XElement(W + "lvl") : new XElement(basis);
            foreach (var e in over.Elements())
            {
                if (e.Name == W + "rPr" && result.Element(e.Name) != null)
                    foreach (var property in e.Elements()) { result.Element(e.Name).Elements(property.Name).Remove(); result.Element(e.Name).Add(new XElement(property)); }
                else { result.Elements(e.Name).Remove(); result.Add(new XElement(e)); }
            }
            return result;
        }

        private static bool CheckPictureHyperlink(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var drawing = FindPictureDrawing(package, RequiredStrings(task, "targetImageSha256s"));
            bool match = drawing != null && PictureGeometryMatches(drawing, task) && drawing.Descendants(A + "hlinkClick").Any(link =>
            {
                string id = (string)link.Attribute(R + "id");
                var rel = package.Xml("word/_rels/document.xml.rels").Root.Elements(Rel + "Relationship")
                    .SingleOrDefault(r => (string)r.Attribute("Id") == id);
                return (string)rel?.Attribute("TargetMode") == "External" &&
                    (string)rel?.Attribute("Type") == R.NamespaceName + "/hyperlink" &&
                    (string)rel?.Attribute("Target") == RequiredString(task, "expectedUrl");
            });
            detail = match ? "The identified logo has the exact external image hyperlink."
                : "The exact hyperlink is missing from the identified logo.";
            return match;
        }

        private static bool PictureGeometryMatches(XElement drawing, TaskDefinition task)
        {
            JToken token;
            if (!task.Extra.TryGetValue("pictureGeometry", out token)) return true; // Existing tasks retain their contract.
            var expected = token as JObject;
            if (expected == null) throw new InvalidDataException("Invalid picture geometry metadata.");
            XElement anchor = drawing.Element(Wp + "anchor");
            if (anchor == null || ParagraphText(drawing.Ancestors(W + "p").First()) != (string)expected["anchorParagraph"]) return false;
            return (string)anchor.Element(Wp + "extent")?.Attribute("cx") == (string)expected["cx"] &&
                (string)anchor.Element(Wp + "extent")?.Attribute("cy") == (string)expected["cy"] &&
                (string)anchor.Element(Wp + "positionH")?.Attribute("relativeFrom") == (string)expected["horizontalRelativeFrom"] &&
                (string)anchor.Element(Wp + "positionH")?.Element(Wp + "posOffset") == (string)expected["horizontalOffset"] &&
                (string)anchor.Element(Wp + "positionV")?.Attribute("relativeFrom") == (string)expected["verticalRelativeFrom"] &&
                (string)anchor.Element(Wp + "positionV")?.Element(Wp + "posOffset") == (string)expected["verticalOffset"] &&
                anchor.Element(Wp + (string)expected["wrapElement"]) != null;
        }

        private static IEnumerable<XElement> StyleChain(XElement styles, string id)
        {
            var chain = new List<XElement>(); var seen = new HashSet<string>();
            while (id != null && seen.Add(id))
            {
                var s = styles.Elements(W + "style").SingleOrDefault(e => (string)e.Attribute(W + "styleId") == id);
                if (s == null) break;
                chain.Add(s); id = (string)s.Element(W + "basedOn")?.Attribute(W + "val");
            }
            chain.Reverse(); return chain;
        }

        private static readonly HashSet<string> ToggleProperties = new HashSet<string>
            { "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow", "emboss", "imprint", "vanish" };
        private static bool On(XElement e) { return e != null && !new[] { "0", "false", "off" }.Contains((string)e.Attribute(W + "val")); }

        private static Dictionary<string, string> EffectiveRunFormatting(PackageSnapshot package, XElement paragraph, XElement run)
        {
            var styles = package.Xml("word/styles.xml").Root;
            var properties = new XElement(W + "rPr");
            Action<XElement, bool> merge = (source, style) =>
            {
                if (source == null) return;
                foreach (var element in source.Elements().Where(e => e.Name != W + "rStyle" && e.Name != W + "rPrChange"))
                {
                    var current = properties.Element(element.Name);
                    if (style && ToggleProperties.Contains(element.Name.LocalName))
                    {
                        if (On(element)) { bool on = !On(current); current?.Remove(); properties.Add(new XElement(element.Name, new XAttribute(W + "val", on ? "1" : "0"))); }
                    }
                    else if (element.Name.LocalName == "rFonts" || element.Name.LocalName == "lang")
                    {
                        if (current == null) { current = new XElement(element.Name); properties.Add(current); }
                        foreach (var attr in element.Attributes()) current.SetAttributeValue(attr.Name, attr.Value);
                        if (element.Name.LocalName == "rFonts")
                            foreach (string slot in new[] { "ascii", "hAnsi", "eastAsia", "cs" })
                            {
                                if (element.Attribute(W + slot) != null) current.Attribute(W + (slot + "Theme"))?.Remove();
                                else if (element.Attribute(W + (slot + "Theme")) != null) current.Attribute(W + slot)?.Remove();
                            }
                    }
                    else { current?.Remove(); properties.Add(new XElement(element)); }
                }
            };
            merge(styles.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr"), false);
            string paragraphStyle = (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ??
                (string)styles.Elements(W + "style").FirstOrDefault(s => (string)s.Attribute(W + "type") == "paragraph" && (string)s.Attribute(W + "default") == "1")?.Attribute(W + "styleId") ?? "Normal";
            foreach (var s in StyleChain(styles, paragraphStyle)) merge(s.Element(W + "rPr"), true);
            string characterStyle = (string)run.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val");
            foreach (var s in StyleChain(styles, characterStyle)) merge(s.Element(W + "rPr"), true);
            merge(run.Element(W + "rPr"), false);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in properties.Elements())
            {
                string name = p.Name.LocalName;
                if (p.Name.Namespace != W)
                {
                    // Text effects are visual formatting too; do not silently ignore them.
                    result[p.Name.ToString()] = p.ToString(SaveOptions.DisableFormatting);
                    continue;
                }
                if (name == "noProof" || name == "lang") continue; // Proofing is not visual formatting.
                if (ToggleProperties.Contains(name) || new[] { "webHidden", "rtl", "cs", "specVanish", "snapToGrid" }.Contains(name))
                { if (On(p)) result[name] = "1"; }
                else if (name == "color") result[name] = ResolveColor(package, p, "val", "themeColor", "themeTint", "themeShade");
                else if (name == "rFonts")
                {
                    foreach (string slot in new[] { "ascii", "hAnsi", "eastAsia", "cs" })
                    {
                        string theme = (string)p.Attribute(W + (slot + "Theme"));
                        string value = (string)p.Attribute(W + slot);
                        if (theme != null)
                        {
                            string scheme = theme.StartsWith("major", StringComparison.Ordinal) ? "majorFont" : "minorFont";
                            string family = slot == "eastAsia" ? "ea" : slot == "cs" ? "cs" : "latin";
                            value = (string)package.Xml("word/theme/theme1.xml").Descendants(A + scheme).Single().Element(A + family)?.Attribute("typeface");
                        }
                        if (!string.IsNullOrEmpty(value)) result["font." + slot] = value;
                    }
                }
                else if (name == "u") { string val = (string)p.Attribute(W + "val") ?? "single"; if (val != "none") result[name] = val; }
                else if (name == "vertAlign") { string val = (string)p.Attribute(W + "val"); if (val != "baseline") result[name] = val; }
                else if (new[] { "spacing", "position", "kern" }.Contains(name)) { string val = (string)p.Attribute(W + "val"); if (val != "0") result[name] = val; }
                else result[name] = string.Join(";", p.Attributes().Where(a => a.Name.Namespace == W).OrderBy(a => a.Name.LocalName).Select(a => a.Name.LocalName + "=" + a.Value));
            }
            return result;
        }

        private static string ResolveColor(PackageSnapshot package, XElement e, string rgb, string theme, string tint, string shade)
        {
            string value = (string)e?.Attribute(W + rgb) ?? "auto";
            string themed = (string)e?.Attribute(W + theme);
            if (themed != null)
            {
                if (themed == "background1") themed = "lt1";
                if (themed == "text1") themed = "dk1";
                XElement c = package.Xml("word/theme/theme1.xml").Descendants(A + "clrScheme").Single().Element(A + themed)?.Elements().FirstOrDefault();
                value = (string)c?.Attribute("lastClr") ?? (string)c?.Attribute("val") ?? value;
            }
            if (value == "auto") value = "000000";
            int parsed;
            if (!int.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                throw new InvalidDataException("Unresolved effective color.");
            string tintValue = (string)e?.Attribute(W + tint), shadeValue = (string)e?.Attribute(W + shade);
            int[] channels = { (parsed >> 16) & 255, (parsed >> 8) & 255, parsed & 255 };
            for (int i = 0; i < channels.Length; i++)
            {
                if (shadeValue != null) channels[i] = channels[i] * Convert.ToInt32(shadeValue, 16) / 255;
                if (tintValue != null) channels[i] = 255 - (255 - channels[i]) * Convert.ToInt32(tintValue, 16) / 255;
            }
            return string.Concat(channels.Select(c => c.ToString("X2")));
        }

        private static bool CheckTextRangeFormatting(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            string expectedText = RequiredString(task, "targetParagraph"), target = RequiredString(task, "targetText");
            var paragraphs = MainParagraphs(package).Where(p => ParagraphText(p) == expectedText).ToList();
            JToken formatting;
            var expected = task.Extra != null && task.Extra.TryGetValue("expectedFormatting", out formatting) ? formatting as JObject : null;
            if (expected == null) throw new InvalidDataException("Missing expected effective formatting.");
            int start = expectedText.IndexOf(target, StringComparison.Ordinal), end = start + target.Length;
            bool match = paragraphs.Count == 1 && start >= 0 && expectedText.IndexOf(target, end, StringComparison.Ordinal) < 0;
            int offset = 0, covered = 0;
            if (match)
                foreach (var run in paragraphs[0].Descendants(W + "r").Where(r => !r.Ancestors(W + "del").Any()))
                {
                    int length = VisibleText(run).Length;
                    int overlap = Math.Max(0, Math.Min(offset + length, end) - Math.Max(offset, start));
                    if (overlap > 0)
                    {
                        var actual = EffectiveRunFormatting(package, paragraphs[0], run);
                        match &= actual.Count == expected.Count && expected.Properties().All(p => actual.ContainsKey(p.Name) && actual[p.Name] == (string)p.Value);
                        covered += overlap;
                    }
                    offset += length;
                }
            match &= covered == target.Length;
            if (match && task.Extra.ContainsKey("expectedAlignment"))
                match = EffectiveParagraphAlignment(package, paragraphs[0]) == RequiredString(task, "expectedAlignment");
            detail = match ? "Every character of the unchanged target range has the verified effective formatting."
                : "The target text changed or one or more characters retain incorrect effective formatting.";
            return match;
        }

        private static bool CheckSavedTemplate(PackageSnapshot package, string path, TaskDefinition task, TrainingGradeContext context, out string detail)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.DefaultTemplateFolder))
                throw new InvalidDataException("Template grading requires the owned document's Save As context.");
            string expectedPath = Path.Combine(context.DefaultTemplateFolder, RequiredString(task, "expectedFileName"));
            bool match = context.SavedFromCurrentWorkingDocument && string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            var types = package.Xml("[Content_Types].xml").Root;
            match &= types.Elements(ct + "Override").Any(t => (string)t.Attribute("PartName") == "/word/document.xml" &&
                (string)t.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml");
            match &= !package.EntryNames.Any(n => n.IndexOf("vba", StringComparison.OrdinalIgnoreCase) >= 0) &&
                !types.Elements().Any(t => ((string)t.Attribute("ContentType") ?? "").IndexOf("macro", StringComparison.OrdinalIgnoreCase) >= 0);
            var mainRelationships = package.Xml("_rels/.rels").Root.Elements(Rel + "Relationship")
                .Where(r => (string)r.Attribute("Type") == R.NamespaceName + "/officeDocument").ToList();
            match &= mainRelationships.Count == 1 && (string)mainRelationships[0].Attribute("TargetMode") != "External" &&
                ((string)mainRelationships[0].Attribute("Target") ?? "").TrimStart('/') == "word/document.xml" &&
                package.Xml("word/document.xml").Root.Name == W + "document";
            var text = MainParagraphs(package).Select(ParagraphText).ToList();
            match &= RequiredStrings(task, "identityParagraphs").All(p => text.Count(t => t == p) == 1);
            match &= FindPictureDrawing(package, RequiredStrings(task, "targetImageSha256s")) != null;
            detail = match ? "The current working document was saved as the correct modern, nonmacro template at Word's default template location."
                : "The template name/location/type/content or current-attempt provenance is incorrect.";
            return match;
        }
    }
}
