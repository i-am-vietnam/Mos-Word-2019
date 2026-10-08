using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static List<XElement> FindTaskTables(XDocument document, TaskDefinition task, string[] header)
        {
            var tables = FindTablesByHeader(document, header);
            if (!task.Extra.ContainsKey("tableSectionHeading")) return tables;
            string heading = RequiredString(task, "tableSectionHeading");
            var body = document.Root.Element(W + "body");
            var children = body.Elements().ToList();
            var headings = children.Where(p => p.Name == W + "p" && ParagraphText(p) == heading).ToList();
            if (headings.Count != 1) return new List<XElement>();
            int start = children.IndexOf(headings[0]);
            string endHeading = task.Extra.ContainsKey("tableSectionEndHeading") ? RequiredString(task, "tableSectionEndHeading") : null;
            var ends = endHeading == null ? new List<XElement>() : children.Where(p => p.Name == W + "p" && ParagraphText(p) == endHeading).ToList();
            if (endHeading != null && (ends.Count != 1 || children.IndexOf(ends[0]) <= start)) return new List<XElement>();
            int end = endHeading == null ? children.Count : children.IndexOf(ends[0]);
            return tables.Where(t => children.IndexOf(t) > start && children.IndexOf(t) < end).ToList();
        }

        private static bool CheckInsertedPicture(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var document = package.Xml("word/document.xml");
            var body = document.Root.Element(W + "body");
            var children = body.Elements().ToList();
            string heading = RequiredString(task, "targetHeading");
            var headings = children.Where(p => p.Name == W + "p" && ParagraphText(p) == heading).ToList();
            detail = "The correct embedded picture must occupy the declared empty paragraph after the unchanged heading and document content.";
            if (headings.Count != 1) return false;
            int index = children.IndexOf(headings[0]) + RequiredInt(task, "paragraphOffsetAfterHeading");
            if (index < 0 || index >= children.Count) return false;
            var paragraph = children[index];
            if (paragraph.Name != W + "p" || !string.IsNullOrEmpty(ParagraphText(paragraph))) return false;
            if (!body.Descendants(W + "p").Where(p => !p.Ancestors(W + "txbxContent").Any()).Select(ParagraphText)
                .Where(t => !string.IsNullOrWhiteSpace(t)).SequenceEqual(RequiredStrings(task, "expectedBodyParagraphs"))) return false;
            if (task.Extra.ContainsKey("expectedBodySymbols"))
            {
                var symbols = body.Descendants(W + "sym").Where(s => !s.Ancestors(W + "del").Any() && !s.Ancestors(W + "txbxContent").Any())
                    .Select(s =>
                    {
                        var row = s.Ancestors(W + "tr").FirstOrDefault();
                        var cell = s.Ancestors(W + "tc").FirstOrDefault();
                        string context = row == null ? ParagraphText(s.Ancestors(W + "p").FirstOrDefault()) :
                            string.Join("\n", row.Elements(W + "tc").First().Elements(W + "p").Select(ParagraphText));
                        int column = row == null ? -1 : row.Elements(W + "tc").ToList().IndexOf(cell);
                        return (string)s.Attribute(W + "font") + "|" + (string)s.Attribute(W + "char") + "|" + context + "|" + column;
                    });
                if (!symbols.SequenceEqual(RequiredStrings(task, "expectedBodySymbols"))) return false;
            }
            var drawings = paragraph.Descendants(W + "drawing").ToList();
            if (drawings.Count != 1) return false;
            XNamespace pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
            var picture = drawings[0].Descendants(pic + "pic").ToList();
            if (picture.Count != 1 || !drawings[0].Elements().Any(e => e.Name == Wp + "inline" || e.Name == Wp + "anchor")) return false;
            var blips = picture[0].Element(pic + "blipFill")?.Elements(A + "blip").ToList();
            if (blips == null || blips.Count != 1 || blips[0].Attribute(R + "link") != null) return false;
            string id = (string)blips[0].Attribute(R + "embed");
            var relationships = package.Xml("word/_rels/document.xml.rels").Root.Elements(Rel + "Relationship")
                .Where(r => (string)r.Attribute("Id") == id).ToList();
            if (relationships.Count != 1 || (string)relationships[0].Attribute("TargetMode") == "External" ||
                (string)relationships[0].Attribute("Type") != R.NamespaceName + "/image") return false;
            string part = package.RelatedPart("word/document.xml", id);
            return !string.IsNullOrEmpty(part) && RequiredStrings(task, "targetImageSha256s")
                .Contains(package.Hash(part), StringComparer.OrdinalIgnoreCase);
        }
    }
}
