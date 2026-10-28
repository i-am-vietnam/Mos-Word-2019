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
        private static bool CheckPreservedSmartArt(PackageSnapshot package, TaskDefinition task)
        {
            JToken token;
            if (!task.Extra.TryGetValue("preservedSmartArt", out token)) return true;
            var preserved = token as JObject;
            var orders = preserved?["allowedLogicalOrders"] as JArray;
            if (orders == null || orders.Count == 0 || orders.Any(o => !(o is JArray) || !o.HasValues))
                throw new InvalidDataException("Preserved SmartArt requires declared logical orders.");
            var definition = new TaskDefinition { Extra = preserved.Properties().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase) };
            foreach (JArray order in orders)
            {
                definition.Extra["expectedLogicalNodes"] = order;
                string detail;
                if (CheckSmartArtLogicalOrder(package, definition, out detail)) return true;
            }
            return false;
        }

        private static bool CheckSmartArtLogicalOrder(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            JArray nodes = RequiredArray(task, "expectedLogicalNodes");
            JArray pictures = RequiredArray(task, "expectedNodePictures");
            string layoutId = RequiredString(task, "expectedLayoutId");
            bool reverse = RequiredBool(task, "expectedReverse");
            var diagrams = AllSmartArts(package);
            detail = "The original SmartArt must retain its layout, logical order, direction and node photographs.";
            if (diagrams.Count != 1) return false;
            var diagram = diagrams[0];
            var run = diagram.Container.Ancestors(W + "r").FirstOrDefault();
            var paragraph = run?.Ancestors(W + "p").FirstOrDefault();
            if (paragraph == null || diagram.Container.Ancestors().Any(e => e.Name == W + "del" || e.Name == W + "moveFrom") ||
                !PositiveDrawingExtent(diagram.Container.Element(Wp + "extent")) ||
                new[] { "vanish", "webHidden", "specVanish" }.Any(EffectiveRunFormatting(package, paragraph, run).ContainsKey)) return false;
            var references = diagram.Container.Descendants(Dgm + "relIds").ToList();
            if (references.Count != 1) return false;
            var ids = references[0];
            string layout = package.RelatedPart("word/document.xml", (string)ids.Attribute(R + "lo"));
            if (layout == null || (string)package.Xml(layout).Root.Attribute("uniqueId") != layoutId) return false;
            var data = package.Xml(diagram.DataPart);
            var directions = data.Descendants(Dgm + "dir").ToList();
            if (directions.Count > 1) return false;
            string direction = (string)directions.FirstOrDefault()?.Attribute("val");
            if (!string.IsNullOrEmpty(direction) && direction != "norm" && direction != "rev") return false;
            return (direction == "rev") == reverse && SmartArtLogicalNodesMatch(data, nodes) &&
                SmartArtPictureAssociationsMatch(package, diagram, pictures);
        }

        private static bool SmartArtPictureAssociationsMatch(PackageSnapshot package, SmartArtReference diagram, JArray expected)
        {
            if (expected == null || expected.Count == 0 || expected.Any(e => !(e is JObject) ||
                string.IsNullOrWhiteSpace((string)e["text"]) || !(e["imageSha256s"] is JArray) ||
                ((JArray)e["imageSha256s"]).Count == 0 || ((JArray)e["imageSha256s"]).Any(h => h.Type != JTokenType.String ||
                    h.ToString().Length != 64 || h.ToString().Any(c => !Uri.IsHexDigit(c)))) ||
                expected.Select(e => (string)e["text"]).Distinct(StringComparer.Ordinal).Count() != expected.Count)
                throw new InvalidDataException("Expected SmartArt node photographs must have unique labels and SHA-256 hashes.");
            var data = package.Xml(diagram.DataPart);
            var points = data.Root.Element(Dgm + "ptLst")?.Elements(Dgm + "pt").ToList();
            if (points == null || points.Any(p => string.IsNullOrEmpty((string)p.Attribute("modelId"))) ||
                points.Select(p => (string)p.Attribute("modelId")).Distinct().Count() != points.Count) return false;
            var byId = points.ToDictionary(p => (string)p.Attribute("modelId"));
            var content = points.Where(p => p.Attribute("type") == null || (string)p.Attribute("type") == "node").ToList();
            if (content.Count != expected.Count) return false;
            var edges = data.Root.Element(Dgm + "cxnLst")?.Elements(Dgm + "cxn").ToList() ?? new List<XElement>();
            var usedPictures = new HashSet<XElement>();
            foreach (var item in expected)
            {
                var nodes = content.Where(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)) == (string)item["text"]).ToList();
                if (nodes.Count != 1) return false;
                string id = (string)nodes[0].Attribute("modelId");
                var presentations = edges.Where(e => (string)e.Attribute("type") == "presOf" && (string)e.Attribute("srcId") == id).ToList();
                if (presentations.Count != 1) return false;
                string textId = (string)presentations[0].Attribute("destId");
                XElement text;
                if (textId == null || !byId.TryGetValue(textId, out text) || (string)text.Attribute("type") != "pres") return false;
                var parents = edges.Where(e => (string)e.Attribute("type") == "presParOf" && (string)e.Attribute("destId") == textId).ToList();
                if (parents.Count != 1) return false;
                string parent = (string)parents[0].Attribute("srcId");
                XElement group;
                if (parent == null || !byId.TryGetValue(parent, out group) || (string)group.Attribute("type") != "pres") return false;
                var siblings = edges.Where(e => (string)e.Attribute("type") == "presParOf" && (string)e.Attribute("srcId") == parent)
                    .Select(e => (string)e.Attribute("destId")).ToList();
                if (siblings.Any(s => s == null || !byId.ContainsKey(s)) || siblings.Distinct().Count() != siblings.Count) return false;
                var images = siblings.Select(s => byId[s]).Where(p => p.Descendants(A + "blip").Any()).ToList();
                if (images.Count != 1 || (string)images[0].Attribute("type") != "pres" || !usedPictures.Add(images[0])) return false;
                if (edges.Count(e => (string)e.Attribute("type") == "presParOf" &&
                    (string)e.Attribute("destId") == (string)images[0].Attribute("modelId")) != 1) return false;
                var blips = images[0].Descendants(A + "blip").ToList();
                if (blips.Count != 1 || blips[0].Attribute(R + "link") != null) return false;
                string imagePart = package.RelatedPart(diagram.DataPart, (string)blips[0].Attribute(R + "embed"));
                string hash = imagePart == null ? null : package.Hash(imagePart);
                if (hash == null || !((JArray)item["imageSha256s"]).Values<string>().Contains(hash, StringComparer.OrdinalIgnoreCase)) return false;
            }
            return points.Where(p => p.Descendants(A + "blip").Any()).All(usedPictures.Contains);
        }
    }
}
