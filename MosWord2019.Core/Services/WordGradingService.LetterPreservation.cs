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
        private static bool CheckUnicodeSymbol(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            int code = RequiredInt(task, "unicodeCodePoint");
            if (code < 1 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF) ||
                (code >= 0xE000 && code <= 0xF8FF) || (code >= 0xF0000 && code <= 0xFFFFD) ||
                (code >= 0x100000 && code <= 0x10FFFD))
                throw new InvalidDataException("The Unicode symbol must be a non-private-use scalar value.");
            string sign = char.ConvertFromUtf32(code);
            string original = RequiredString(task, "expectedSentence");
            string reference = task.Extra.ContainsKey("referenceText") ? RequiredString(task, "referenceText") : RequiredString(task, "anchorText");
            string placement = task.Extra.ContainsKey("placement") ? RequiredString(task, "placement") : "before";
            int offset = original.IndexOf(reference, StringComparison.Ordinal);
            if ((placement != "before" && placement != "after") || original.Contains(sign) || offset < 0 ||
                original.IndexOf(reference, offset + reference.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Invalid Unicode symbol placement or unchanged paragraph metadata.");
            if (placement == "after") offset += reference.Length;
            string expected = original.Insert(offset, sign);
            var paragraphs = package.Xml("word/document.xml").Root.Element(W + "body").Elements(W + "p")
                .Where(p => LogicalParagraphText(p).Replace(sign, "") == original).ToList();
            bool match = paragraphs.Count == 1 && LogicalParagraphText(paragraphs[0]) == expected &&
                LetterParagraphContentVisible(package, paragraphs[0]) && CheckPreservedStoryObjects(package, task);
            detail = match ? "The Unicode symbol is immediately " + placement + " the unchanged reference text."
                : "The Unicode symbol, its position or the complete original paragraph differs.";
            return match;
        }

        private static bool LetterParagraphContentVisible(PackageSnapshot package, XElement paragraph, bool allowFields = false)
        {
            if (paragraph.Descendants().Any(e => e.Name == W + "sym" || e.Name == W + "del" || e.Name == W + "moveFrom" ||
                e.Name == W + "drawing" || e.Name == W + "pict" || e.Name == W + "object" || e.Name == W + "txbxContent" ||
                (!allowFields && (e.Name == W + "fldChar" || e.Name == W + "fldSimple" || e.Name == W + "instrText")) ||
                e.Name == W + "delText" || e.Name.NamespaceName == "http://schemas.openxmlformats.org/officeDocument/2006/math" ||
                new[] { "noBreakHyphen", "softHyphen", "ptab", "footnoteReference", "endnoteReference", "footnoteRef", "endnoteRef", "pgNum", "contentPart", "subDoc" }
                    .Any(name => e.Name == W + name))) return false;
            return paragraph.Descendants(W + "r").Where(r => r.Descendants().Any(e => e.Name == W + "t" || e.Name == W + "tab" || e.Name == W + "br" || e.Name == W + "cr"))
                .All(r => !new[] { "vanish", "webHidden", "specVanish" }.Any(EffectiveRunFormatting(package, paragraph, r).ContainsKey));
        }

        private static bool CheckDeclaredLetterPreservation(PackageSnapshot package, TaskDefinition task)
        {
            if (!task.Extra.ContainsKey("expectedLetterParagraphs")) return true;
            var declaration = RequiredArray(task, "expectedLetterParagraphs");
            if (declaration.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t)))
                throw new InvalidDataException("The original letter requires exact nonempty logical paragraphs.");
            string[] expected = declaration.Values<string>().ToArray();
            int tableAfter = RequiredInt(task, "letterTableAfterParagraphIndex");
            if (tableAfter < 0 || tableAfter >= expected.Length - 1)
                throw new InvalidDataException("Invalid original letter table position.");
            var bullets = RequiredArray(task, "letterBulletedParagraphIndices");
            if (bullets.Any(i => i.Type != JTokenType.Integer || (int)i < 0 || (int)i >= expected.Length) ||
                bullets.Values<int>().Distinct().Count() != bullets.Count)
                throw new InvalidDataException("Invalid original letter bullet indices.");
            int bulletLevel = RequiredInt(task, "letterBulletLevel");
            string bulletText = RequiredString(task, "letterBulletText");
            if (bulletLevel < 0 || bulletLevel > 8) throw new InvalidDataException("Invalid original letter bullet level.");
            var variants = task.Extra.ContainsKey("letterParagraphVariants") ? task.Extra["letterParagraphVariants"] as JObject : new JObject();
            int index;
            if (variants == null || variants.Properties().Any(v => !int.TryParse(v.Name, out index) || index < 0 || index >= expected.Length ||
                !(v.Value is JArray) || !v.Value.HasValues || ((JArray)v.Value).Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))))
                throw new InvalidDataException("Invalid original letter paragraph variants.");

            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            var children = body.Elements().ToList();
            var covers = children.Where(e => e.Name == W + "sdt").ToList();
            if (covers.Count > 0)
            {
                if (covers.Count != 1 || !task.Extra.ContainsKey("expectedCover") || !CheckDeclaredCoverAssembly(package, task, false) ||
                    children.TakeWhile(e => e != covers[0]).Any(e => e.Name != W + "p" || !string.IsNullOrWhiteSpace(LogicalParagraphText(e)))) return false;
                children.Remove(covers[0]);
            }
            if (children.Any(e => e.Name != W + "p" && e.Name != W + "tbl" && e.Name != W + "sectPr" &&
                e.Name != W + "bookmarkStart" && e.Name != W + "bookmarkEnd")) return false;
            var rows = RequiredStringMatrix(task, "expectedTableRows");
            if (rows.Any(row => row.Length != rows[0].Length))
                throw new InvalidDataException("The original letter table must be rectangular.");
            var tables = children.Where(e => e.Name == W + "tbl").ToList();
            if (tables.Count != 1 || !DeclaredTableMatchesExact(package, tables[0], rows)) return false;
            var paragraphs = children.Where(e => e.Name == W + "p" && !string.IsNullOrWhiteSpace(LogicalParagraphText(e))).ToList();
            var possibleCaptions = paragraphs.Where(p => LogicalParagraphText(p) == CaptionExpectedText(task)).ToList();
            if (possibleCaptions.Count > 0)
            {
                if (possibleCaptions.Count != 1 || !CaptionParagraphMatches(package, possibleCaptions[0], task) ||
                    children.IndexOf(possibleCaptions[0]) <= children.IndexOf(tables[0]) ||
                    children.Skip(children.IndexOf(tables[0]) + 1).TakeWhile(e => e != possibleCaptions[0]).Any(e => !IsBlankTableBoundary(e))) return false;
                paragraphs.Remove(possibleCaptions[0]);
            }
            if (paragraphs.Count != expected.Length || paragraphs.Count(p => XNode.CompareDocumentOrder(p, tables[0]) < 0) != tableAfter + 1) return false;
            for (int i = 0; i < paragraphs.Count; i++)
            {
                string actual = LogicalParagraphText(paragraphs[i]);
                var alternatives = variants[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JArray;
                if ((actual != expected[i] && (alternatives == null || !alternatives.Values<string>().Contains(actual))) ||
                    !LetterParagraphContentVisible(package, paragraphs[i])) return false;
                XElement level = EffectiveNumberingLevel(package, paragraphs[i], new HashSet<string>());
                if (bullets.Values<int>().Contains(i) ? ((string)level?.Element(W + "numFmt")?.Attribute(W + "val") != "bullet" ||
                    (string)level?.Attribute(W + "ilvl") != bulletLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                    (string)level?.Element(W + "lvlText")?.Attribute(W + "val") != bulletText) : level != null) return false;
            }
            return children.Where(e => e.Name == W + "p" && string.IsNullOrWhiteSpace(LogicalParagraphText(e)))
                .All(p => LogicalParagraphText(p).Length == 0 && LetterParagraphContentVisible(package, p));
        }
    }
}
