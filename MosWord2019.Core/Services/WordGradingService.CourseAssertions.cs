using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static string PreviousNonemptyParagraph(XElement element)
        {
            return element?.ElementsBeforeSelf().Where(e => e.Name == W + "p")
                .Select(ParagraphText).LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }

        private static bool TableCellsMatchWidths(XElement table, IList<int> grid, int tolerance)
        {
            if (grid.Count == 0 || grid.Any(w => w <= 0)) return false;
            foreach (var row in table.Elements(W + "tr"))
            {
                var cells = row.Elements(W + "tc").ToList();
                if (cells.Count != grid.Count || cells.Any(c => c.Element(W + "tcPr")?.Element(W + "gridSpan") != null ||
                    c.Element(W + "tcPr")?.Element(W + "vMerge") != null)) return false;
                for (int i = 0; i < grid.Count; i++)
                {
                    var width = cells[i].Element(W + "tcPr")?.Element(W + "tcW");
                    string type = (string)width?.Attribute(W + "type");
                    int value = Twips(width, "w", -1);
                    if (type == "dxa") { if (Math.Abs(value - grid[i]) > tolerance) return false; }
                    else if (type == "pct")
                    {
                        // Native SetWidth preserves percentage cell shares; grid records actual reopened widths.
                        if (Math.Abs(value - 5000.0 * grid[i] / grid.Sum()) > 1) return false;
                    }
                    else return false;
                }
            }
            return true;
        }

        private static bool CheckInsertedAutoFitTable(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            var anchors = body.Elements(W + "p").Where(p => MatchesDeclaredText(task, "precedingParagraph", ParagraphText(p))).ToList();
            bool match = anchors.Count == 1 && MatchesDeclaredText(task, "precedingHeading", PreviousNonemptyParagraph(anchors[0]));
            bool blankBoundaries = OptionalBool(task, "allowBlankBoundaries", false);
            XElement table = match ? anchors[0].ElementsAfterSelf().FirstOrDefault(e => !blankBoundaries || !IsBlankTableBoundary(e)) : null;
            if (table?.Name != W + "tbl") match = false;
            if (match)
            {
                var following = table.ElementsAfterSelf().FirstOrDefault(e => blankBoundaries ? !IsBlankTableBoundary(e) :
                    e.Name != W + "p" || !string.IsNullOrWhiteSpace(ParagraphText(e)));
                var properties = table.Element(W + "tblPr");
                var width = properties?.Element(W + "tblW");
                string layout = (string)properties?.Element(W + "tblLayout")?.Attribute(W + "type");
                match = following?.Name == W + "p" && ParagraphText(following) == RequiredString(task, "followingHeading") &&
                    TableRowsEqual(table, RequiredStringMatrix(task, "expectedTableRows")) &&
                    !table.Descendants().Any(e => e.Name == W + "gridSpan" || e.Name == W + "vMerge" || e.Name == W + "hMerge") &&
                    table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() == (task.Extra.ContainsKey("expectedColumns") ? RequiredInt(task, "expectedColumns") : 2) &&
                    (layout == null || layout == "autofit") && (string)width?.Attribute(W + "type") == "auto" && Twips(width, "w", -1) == 0 &&
                    table.Elements(W + "tr").SelectMany(r => r.Elements(W + "tc")).All(c =>
                        (string)c.Element(W + "tcPr")?.Element(W + "tcW")?.Attribute(W + "type") == "auto" &&
                        Twips(c.Element(W + "tcPr")?.Element(W + "tcW"), "w", -1) == 0);
            }
            detail = match ? "The unchanged table has the declared dimensions and native AutoFit Contents at the required boundary."
                : "The table location, cells, dimensions or AutoFit Contents state is incorrect.";
            return match;
        }

        private static bool MatchesDeclaredText(TaskDefinition task, string key, string actual)
        {
            return actual == RequiredString(task, key) || (task.Extra.ContainsKey(key + "Alternatives") &&
                RequiredStrings(task, key + "Alternatives").Contains(actual));
        }

        private static bool CheckParagraphEndSectionBreak(PackageSnapshot package, TaskDefinition task, out string detail, string expectedType = "continuous")
        {
            if (!new[] { "nextPage", "continuous", "oddPage", "evenPage" }.Contains(expectedType))
                throw new InvalidDataException("Invalid expected section break type.");
            var document = package.Xml("word/document.xml");
            var children = document.Root.Element(W + "body").Elements().ToList();
            var anchors = children.Where(p => p.Name == W + "p" &&
                MatchesDeclaredText(task, "precedingParagraph", ParagraphTextOutsideTextBoxes(p))).ToList();
            var headings = children.Where(p => p.Name == W + "p" &&
                ParagraphTextOutsideTextBoxes(p) == RequiredString(task, "targetHeading")).ToList();
            bool match = anchors.Count == 1 && headings.Count == 1 && children.IndexOf(anchors[0]) < children.IndexOf(headings[0]);
            if (match)
            {
                int start = children.IndexOf(anchors[0]), end = children.IndexOf(headings[0]);
                var between = children.Skip(start + 1).Take(end - start - 1).ToList();
                match = between.All(IsBlankTableBoundary);
                var boundaries = new[] { anchors[0] }.Concat(between).Where(p => p.Name == W + "p" && p.Element(W + "pPr")?.Element(W + "sectPr") != null).ToList();
                var sections = DocumentSections(document);
                match &= boundaries.Count > 0 && boundaries.All(p =>
                {
                    int index = sections.FindIndex(s => s.Content.Contains(p));
                    return index >= 0 && index + 1 < sections.Count &&
                        ((string)sections[index + 1].Properties.Element(W + "type")?.Attribute(W + "val") ?? "nextPage") == expectedType;
                });
            }
            detail = match ? "A real " + expectedType + " section boundary follows the complete unchanged narrative before the declared heading."
                : "The paragraph-end section boundary is absent, misplaced, has the wrong type or its narrative changed.";
            return match;
        }

        private static bool CheckMainStoryColumnBlock(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var expected = RequiredStrings(task, "targetParagraphs");
            var document = package.Xml("word/document.xml");
            var paragraphs = document.Root.Element(W + "body").Elements(W + "p").ToList();
            if (task.Extra.ContainsKey("sectionStartText"))
                paragraphs = (DeclaredBodySection(document, task) ?? new List<XElement>()).Where(p => p.Name == W + "p").ToList();
            var alternatives = task.Extra.ContainsKey("paragraphTextAlternatives") ? task.Extra["paragraphTextAlternatives"] as JObject : new JObject();
            if (alternatives == null || alternatives.Properties().Any(p => !(p.Value is JArray) || ((JArray)p.Value).Any(v => v.Type != JTokenType.String)))
                throw new InvalidDataException("Invalid column paragraph alternatives.");
            Func<string, string, bool> accepts = (canonical, actual) => canonical == actual ||
                (alternatives[canonical] is JArray && ((JArray)alternatives[canonical]).Values<string>().Contains(actual));
            var targets = expected.Select(text => paragraphs.Where(p => accepts(text, ParagraphTextOutsideTextBoxes(p))).ToList()).ToList();
            bool match = targets.All(t => t.Count == 1);
            if (match)
            {
                var selected = targets.Select(t => t[0]).ToList();
                var sections = DocumentSections(document);
                var containing = sections.Where(s => selected.All(s.Content.Contains)).ToList();
                match = selected.Distinct().Count() == expected.Length && containing.Count == 1;
                if (match)
                {
                    var section = containing[0];
                    match = section.Content.Where(p => p.Name == W + "p" && ParagraphTextOutsideTextBoxes(p).Length > 0).SequenceEqual(selected) &&
                        ColumnCount(section.Properties) == RequiredInt(task, "expectedColumns") &&
                        OutsideColumnSectionsMatch(sections.Where(s => s != section).ToList(), task);
                    if (OptionalBool(task, "checkColumnSpacing", true))
                        match &= Twips(section.Properties.Element(W + "cols"), "space", -1) == RequiredInt(task, "expectedColumnSpacingTwips");
                    if (task.Extra.ContainsKey("preservedDropCap"))
                    {
                        var drop = task.Extra["preservedDropCap"] as JObject;
                        if (drop == null) throw new InvalidDataException("Invalid Drop Cap metadata.");
                        var p = selected.SingleOrDefault(x => ParagraphTextOutsideTextBoxes(x) == (string)drop["text"]);
                        var frame = p?.Element(W + "pPr")?.Element(W + "framePr");
                        match &= frame != null && (string)frame.Attribute(W + "dropCap") == (string)drop["dropCap"] &&
                            (string)frame.Attribute(W + "lines") == (string)drop["lines"];
                    }
                }
            }
            match &= CheckPreservedStoryObjects(package, task);
            detail = match ? "Exactly the declared main-story paragraphs occupy the requested columns; surrounding sections retain their declared layout."
                : "The narrative, Drop Cap, column scope/count/spacing or protected story content is incorrect.";
            return match;
        }

        private static bool CheckPreservedStoryObjects(PackageSnapshot package, TaskDefinition task)
        {
            bool match = true;
            var body = package.Xml("word/document.xml").Root.Element(W + "body");
            if (task.Extra.ContainsKey("preservedBodyParagraphs"))
            {
                var declared = RequiredArray(task, "preservedBodyParagraphs");
                if (declared.Any(t => t.Type != JTokenType.String && !(t is JArray)) ||
                    declared.OfType<JArray>().Any(a => a.Count == 0 || a.Any(t => t.Type != JTokenType.String)))
                    throw new InvalidDataException("Invalid preserved body paragraph metadata.");
                var generated = GeneratedTocParagraphs(body);
                var actual = body.Descendants(W + "p").Where(p => !p.Ancestors(W + "tbl").Any() &&
                    !p.Ancestors(W + "txbxContent").Any() && !generated.Contains(p))
                    .Select(p => OptionalBool(task, "ignoreCitationsInPreservedText", false)
                        ? ParagraphTextOutsideCitations(p) : ParagraphTextOutsideTextBoxes(p)).Where(t => t.Length > 0).ToList();
                match &= actual.Count == declared.Count && actual.Select((text, i) => i < declared.Count &&
                    (declared[i].Type == JTokenType.String ? text == (string)declared[i] : ((JArray)declared[i]).Values<string>().Contains(text))).All(v => v);
            }
            if (task.Extra.ContainsKey("preservedStoryParagraphs"))
                match &= body.Descendants(W + "p").Where(p => !p.Ancestors(W + "tbl").Any() && !p.Ancestors(W + "txbxContent").Any())
                    .Select(StoryParagraphText).Where(t => t.Length > 0).SequenceEqual(RequiredStrings(task, "preservedStoryParagraphs"));
            if (task.Extra.ContainsKey("preservedCustomXml"))
                foreach (JObject expected in RequiredArray(task, "preservedCustomXml"))
                    match &= package.EntryNames.Where(n => n.StartsWith("customXml/item", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal))
                        .Select(n => package.Xml(n).Root).Count(root => root.Name.ToString() == (string)expected["root"] &&
                            string.Concat(root.DescendantNodes().OfType<XText>().Select(t => t.Value)) == (string)expected["text"]) == 1;
            if (task.Extra.ContainsKey("protectedTableRows"))
            {
                var tables = body.Descendants(W + "tbl").ToList();
                var declared = RequiredArray(task, "protectedTableRows");
                match &= tables.Count == declared.Count;
                foreach (var rows in declared)
                {
                    if (!(rows is JArray) || ((JArray)rows).Any(row => !(row is JArray) || ((JArray)row).Any(t => t.Type != JTokenType.String)))
                        throw new InvalidDataException("Invalid protected table rows.");
                    var cells = ((JArray)rows).Select(row => row.Values<string>().ToArray()).ToArray();
                    match &= tables.Count(table => TableRowsEqual(table, cells)) == 1;
                }
            }
            if (task.Extra.ContainsKey("preservedBodySequences"))
            {
                var generated = OptionalBool(task, "ignoreGeneratedTocInPreservedBody", false) ? GeneratedTocParagraphs(body) : new HashSet<XElement>();
                var actual = body.Descendants(W + "p").Where(p => !p.Ancestors(W + "tbl").Any() && !p.Ancestors(W + "txbxContent").Any())
                    .Where(p => !generated.Contains(p)).Select(p => OptionalBool(task, "preserveStoryWhitespace", false)
                        ? LogicalParagraphText(p) : ParagraphTextOutsideTextBoxes(p))
                    .Where(t => OptionalBool(task, "preserveStoryWhitespace", false) ? t.Trim().Length > 0 : t.Length > 0).ToList();
                var sequences = RequiredArray(task, "preservedBodySequences");
                if (sequences.Any(s => !(s is JArray) || ((JArray)s).Any(t => t.Type != JTokenType.String)))
                    throw new InvalidDataException("Invalid preserved body sequences.");
                match &= sequences.Cast<JArray>().Any(s => actual.SequenceEqual(s.Values<string>()));
            }
            if (task.Extra.ContainsKey("protectedParagraphBreaks"))
                foreach (JObject expected in RequiredArray(task, "protectedParagraphBreaks"))
                {
                    var paragraphs = body.Elements(W + "p").Where(p => VisibleText(p) == (string)expected["text"]).ToList();
                    if (paragraphs.Count != 1) return false;
                    var breaks = paragraphs[0].Descendants(W + "br").ToList();
                    match &= breaks.Select(b => (string)b.Attribute(W + "type") ?? "textWrapping").SequenceEqual(((JArray)expected["types"]).Values<string>()) &&
                        breaks.All(b => !b.ElementsBeforeSelf(W + "t").Any() && !b.Parent.ElementsBeforeSelf(W + "r").Descendants(W + "t").Any());
                }
            if (task.Extra.ContainsKey("protectedPictureSha256s"))
                match &= RequiredStrings(task, "protectedPictureSha256s").All(hash => FindPictureDrawing(package, new[] { hash }) != null);
            if (task.Extra.ContainsKey("protectedTextBoxParagraphs"))
            {
                var text = ScopedStoryParagraphs(package.Xml("word/document.xml"), "TextBox")
                    .Select(StoryParagraphText).Where(t => t.Length > 0).Distinct().ToList();
                match &= text.OrderBy(t => t, StringComparer.Ordinal).SequenceEqual(RequiredStrings(task, "protectedTextBoxParagraphs").Distinct().OrderBy(t => t, StringComparer.Ordinal));
            }
            return match && CheckDeclaredDocumentPreservation(package, task) && PreservedBookmarksMatch(body.Document, task) && PreservedParagraphFormatsMatch(package, task) && CheckDeclaredObjectPreservation(package, task);
        }

        private static bool IsBlankTableBoundary(XElement element)
        {
            return element.Name == W + "bookmarkStart" || element.Name == W + "bookmarkEnd" ||
                (element.Name == W + "p" && string.IsNullOrWhiteSpace(ParagraphText(element)) &&
                 !element.Descendants().Any(e => e.Name == W + "drawing" || e.Name == W + "pict" ||
                    e.Name == W + "object" || e.Name == W + "tab" || e.Name == W + "br" || e.Name == W + "fldChar"));
        }

        private static bool CheckEffectiveContinuedSequence(PackageSnapshot package, TaskDefinition task,
            List<XElement> paragraphs, string[] expected, out string detail)
        {
            var alternatives = task.Extra.ContainsKey("paragraphTextAlternatives") ? task.Extra["paragraphTextAlternatives"] as JObject : null;
            if (task.Extra.ContainsKey("paragraphTextAlternatives") && (alternatives == null || alternatives.Properties()
                .Any(p => !(p.Value is JArray) || ((JArray)p.Value).Any(v => v.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)v)))))
                throw new InvalidDataException("Invalid numbered paragraph alternatives metadata.");
            var targets = new List<XElement>();
            foreach (string text in expected)
            {
                var found = paragraphs.Where(p => ParagraphText(p) == text ||
                    (alternatives?[text] is JArray && ((JArray)alternatives[text]).Values<string>().Contains(ParagraphText(p)))).ToList();
                if (found.Count != 1 || targets.Contains(found[0]) ||
                    (targets.Count > 0 && paragraphs.IndexOf(found[0]) <= paragraphs.IndexOf(targets.Last())))
                { detail = "The exact numbered items are missing, duplicated or out of order."; return false; }
                targets.Add(found[0]);
            }
            int levelIndex = RequiredInt(task, "expectedLevel");
            string id = null;
            foreach (var p in targets)
            {
                var properties = EffectiveNumberProperties(package, p);
                string current = (string)properties.Element(W + "numId")?.Attribute(W + "val");
                var level = EffectiveNumberingLevel(package, p, new HashSet<string>());
                if (current == null || current == "0" || (id != null && current != id) ||
                    Twips(properties.Element(W + "ilvl"), "val", 0) != levelIndex ||
                    (string)level?.Element(W + "numFmt")?.Attribute(W + "val") != RequiredString(task, "expectedNumberFormat") ||
                    (string)level?.Element(W + "lvlText")?.Attribute(W + "val") != RequiredString(task, "expectedLevelText"))
                { detail = "The items do not share the required effective Word numbering."; return false; }
                id = current;
            }
            int initial = NumberingSequenceStart(package, id, levelIndex, new HashSet<string>()), value = initial;
            int targetIndex = 0;
            int scopeEnd = paragraphs.Count - 1;
            if (task.Extra.ContainsKey("numberingScopeEndParagraph"))
            {
                var ends = paragraphs.Where(p => MatchesDeclaredText(task, "numberingScopeEndParagraph", ParagraphText(p))).ToList();
                if (ends.Count != 1 || paragraphs.IndexOf(ends[0]) < paragraphs.IndexOf(targets.Last()))
                { detail = "The numbering scope boundary is missing or changed."; return false; }
                scopeEnd = paragraphs.IndexOf(ends[0]);
            }
            foreach (var p in paragraphs)
            {
                var pr = EffectiveNumberProperties(package, p);
                string current = (string)pr.Element(W + "numId")?.Attribute(W + "val");
                if (OptionalBool(task, "requireOnlyTargetNumbering", false) && paragraphs.IndexOf(p) >= paragraphs.IndexOf(targets[0]) &&
                    paragraphs.IndexOf(p) <= scopeEnd && current != null && current != "0" && !targets.Contains(p))
                { detail = "A paragraph outside the declared items was numbered."; return false; }
                if (current != id) continue;
                int currentLevel = Twips(pr.Element(W + "ilvl"), "val", 0);
                if (currentLevel < levelIndex) value = initial;
                else if (currentLevel == levelIndex)
                {
                    if (targets.Contains(p) && value != ++targetIndex)
                    { detail = "The effective list numbers do not continue from 1 through " + expected.Length + "."; return false; }
                    value++;
                }
            }
            bool match = targetIndex == expected.Length;
            detail = match ? "The exact items form one effective Word sequence from 1 through " + expected.Length + "." : "The numbering sequence is incomplete.";
            return match;
        }

        private static XElement EffectiveNumberProperties(PackageSnapshot package, XElement paragraph)
        {
            var styles = package.Xml("word/styles.xml").Root;
            var result = new XElement(W + "numPr");
            foreach (var properties in StyleChain(styles, (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "Normal")
                .Select(s => s.Element(W + "pPr")?.Element(W + "numPr")).Concat(new[] { paragraph.Element(W + "pPr")?.Element(W + "numPr") }).Where(p => p != null))
                foreach (var e in properties.Elements()) { result.Elements(e.Name).Remove(); result.Add(new XElement(e)); }
            return result;
        }

        private static bool CheckExactBulletedList(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var paragraphs = MainParagraphs(package);
            string[] texts = RequiredStrings(task, "targetParagraphs");
            var indexes = texts.Select(t => paragraphs.FindIndex(p => ParagraphText(p) == t)).ToList();
            if (indexes.Count == 0 || indexes.Any(i => i < 0) ||
                texts.Any(t => paragraphs.Count(p => ParagraphText(p) == t) != 1) ||
                indexes.Where((n, i) => i > 0 && n != indexes[i - 1] + 1).Any())
            { detail = "The exact list items are not one unique consecutive paragraph block."; return false; }
            bool geometry = OptionalBool(task, "checkIndentation", true);
            if (task.Extra.ContainsKey("sectionStartText"))
            {
                var section = DeclaredBodySection(paragraphs[0].Document, task);
                if (section == null || indexes.Any(i => !section.Contains(BodyChild(paragraphs[i]))))
                { detail = "The bullet items are outside their declared original section."; return false; }
            }
            string listId = null, listLevel = null;
            XElement styles = package.Xml("word/styles.xml").Root;
            foreach (int index in indexes)
            {
                XElement paragraph = paragraphs[index];
                XElement number = EffectiveNumberProperties(package, paragraph);
                string id = (string)number.Element(W + "numId")?.Attribute(W + "val");
                string levelId = (string)number.Element(W + "ilvl")?.Attribute(W + "val") ?? "0";
                XElement level = EffectiveNumberingLevel(package, paragraph, new HashSet<string>());
                if (level == null || (string)level.Element(W + "numFmt")?.Attribute(W + "val") != "bullet" ||
                    (listId != null && (listId != id || listLevel != levelId)))
                { detail = "Every item must belong to the same genuine bullet list and level."; return false; }
                listId = id; listLevel = levelId;
                if (!geometry) continue;
                // Resolve style/default indentation, then the numbering level and direct overrides.
                XElement indent = new XElement(W + "ind");
                foreach (var ind in new[] { styles.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr")?.Element(W + "ind") }
                    .Concat(StyleChain(styles, (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "Normal")
                        .Select(s => s.Element(W + "pPr")?.Element(W + "ind")))
                    .Concat(new[] { level.Element(W + "pPr")?.Element(W + "ind"), paragraph.Element(W + "pPr")?.Element(W + "ind") }).Where(e => e != null))
                {
                    foreach (var a in ind.Attributes()) indent.SetAttributeValue(a.Name, a.Value);
                    if (ind.Attribute(W + "firstLine") != null) indent.Attribute(W + "hanging")?.Remove();
                    if (ind.Attribute(W + "hanging") != null) indent.Attribute(W + "firstLine")?.Remove();
                    if (ind.Attribute(W + "left") != null) indent.Attribute(W + "start")?.Remove();
                }
                int left = Twips(indent, "start", Twips(indent, "left", 0));
                int hanging = Twips(indent, "hanging", -Twips(indent, "firstLine", 0));
                bool correct = left - hanging == RequiredInt(task, "expectedBulletPositionTwips");
                if (task.Extra.ContainsKey("expectedHangingDistanceTwips"))
                    correct &= hanging == RequiredInt(task, "expectedHangingDistanceTwips");
                else correct &= left == RequiredInt(task, "expectedTextIndentTwips");
                if (!correct) { detail = "Bullet position or preserved hanging distance differs."; return false; }
            }
            if ((indexes[0] > 0 && HasNumbering(paragraphs[indexes[0] - 1])) ||
                (indexes.Last() + 1 < paragraphs.Count && HasNumbering(paragraphs[indexes.Last() + 1])))
            { detail = "The list includes a neighboring paragraph outside the target block."; return false; }
            detail = geometry ? "The exact bullet list has the required position and hanging distance." : "All exact items form one genuine bullet list.";
            return true;
        }

        private static bool CheckNumberedSequence(PackageSnapshot package, TaskDefinition task, out string detail)
        {
            var paragraphs = MainParagraphs(package);
            var texts = RequiredStrings(task, "expectedParagraphs");
            int anchor = paragraphs.FindIndex(p => ParagraphText(p) == RequiredString(task, "precedingParagraph"));
            var targets = paragraphs.Skip(anchor + 1).Take(texts.Length).ToList();
            bool match = anchor >= 0 && targets.Select(ParagraphText).SequenceEqual(texts) &&
                texts.All(t => paragraphs.Count(p => ParagraphText(p) == t) == 1);
            int expectedLevel = RequiredInt(task, "expectedLevel"), start = RequiredInt(task, "expectedStart");
            string id = null;
            foreach (var p in targets)
            {
                var properties = EffectiveNumberProperties(package, p);
                string current = (string)properties.Element(W + "numId")?.Attribute(W + "val");
                var level = EffectiveNumberingLevel(package, p, new HashSet<string>());
                match &= current != null && current != "0" && (id == null || id == current) && Twips(properties.Element(W + "ilvl"), "val", 0) == expectedLevel &&
                    (string)level?.Element(W + "numFmt")?.Attribute(W + "val") == RequiredString(task, "expectedNumberFormat") &&
                    (string)level?.Element(W + "lvlText")?.Attribute(W + "val") == RequiredString(task, "expectedLevelText");
                if (id == null) id = current;
            }
            if (match)
            {
                int initial = NumberingSequenceStart(package, id, expectedLevel, new HashSet<string>());
                int actual = initial;
                // Count earlier items in this same numbering instance; lower levels reset this level.
                foreach (var p in paragraphs.TakeWhile(p => p != targets[0]))
                {
                    var pr = EffectiveNumberProperties(package, p);
                    if ((string)pr.Element(W + "numId")?.Attribute(W + "val") != id) continue;
                    int levelIndex = Twips(pr.Element(W + "ilvl"), "val", 0);
                    if (levelIndex == expectedLevel) actual++;
                    else if (levelIndex < expectedLevel) actual = initial;
                }
                match &= actual == start;
            }
            detail = match ? "The unchanged list is one real decimal sequence starting at the declared value."
                : "The list text, effective numbering or continued sequence is incorrect.";
            return match;
        }

        private static int NumberingSequenceStart(PackageSnapshot package, string id, int index, HashSet<string> visited)
        {
            if (id == null || !visited.Add(id)) throw new InvalidDataException("Invalid linked numbering definition.");
            var numbering = package.Xml("word/numbering.xml").Root;
            var num = numbering.Elements(W + "num").SingleOrDefault(n => (string)n.Attribute(W + "numId") == id);
            var over = num?.Elements(W + "lvlOverride").SingleOrDefault(e => Twips(e, "ilvl", -1) == index);
            if (over?.Element(W + "startOverride") != null) return Twips(over.Element(W + "startOverride"), "val", 1);
            if (over?.Element(W + "lvl") != null) return Twips(over.Element(W + "lvl").Element(W + "start"), "val", 1);
            string abstractId = (string)num?.Element(W + "abstractNumId")?.Attribute(W + "val");
            var definition = numbering.Elements(W + "abstractNum").SingleOrDefault(n => (string)n.Attribute(W + "abstractNumId") == abstractId);
            string link = (string)definition?.Element(W + "numStyleLink")?.Attribute(W + "val");
            if (link != null)
            {
                string linkedId = StyleChain(package.Xml("word/styles.xml").Root, link)
                    .Select(s => (string)s.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val"))
                    .LastOrDefault(value => value != null);
                return NumberingSequenceStart(package, linkedId, index, visited);
            }
            return Twips(definition?.Elements(W + "lvl").SingleOrDefault(e => Twips(e, "ilvl", -1) == index)?.Element(W + "start"), "val", 1);
        }

        private static bool SmartArtLogicalNodesMatch(XDocument data, JArray expected)
        {
            if (expected == null || expected.Count == 0) throw new InvalidDataException("Expected logical SmartArt nodes are missing.");
            var points = data.Root.Element(Dgm + "ptLst")?.Elements(Dgm + "pt").ToList();
            if (points == null || points.Any(p => p.Attribute("modelId") == null) || points.Select(p => (string)p.Attribute("modelId")).Distinct().Count() != points.Count) return false;
            var byId = points.ToDictionary(p => (string)p.Attribute("modelId"));
            var roots = points.Where(p => (string)p.Attribute("type") == "doc").ToList();
            if (roots.Count != 1) return false;
            var edges = data.Root.Element(Dgm + "cxnLst")?.Elements(Dgm + "cxn")
                .Where(e => e.Attribute("type") == null || (string)e.Attribute("type") == "parOf").ToList() ?? new List<XElement>();
            var visited = new HashSet<string>();
            Func<string, JArray, bool> walk = null;
            walk = (parent, required) =>
            {
                var children = edges.Where(e => (string)e.Attribute("srcId") == parent).ToList();
                int value;
                if (children.Any(e => !int.TryParse((string)e.Attribute("srcOrd") ?? "0", out value)) ||
                    children.Select(e => (string)e.Attribute("srcOrd") ?? "0").Distinct().Count() != children.Count) return false;
                children = children.OrderBy(e => int.Parse((string)e.Attribute("srcOrd") ?? "0")).ToList();
                if (children.Count != required.Count) return false;
                for (int i = 0; i < children.Count; i++)
                {
                    string id = (string)children[i].Attribute("destId");
                    XElement p;
                    if (id == null || !byId.TryGetValue(id, out p) || !visited.Add(id) ||
                        string.Concat(p.Descendants(A + "t").Select(t => t.Value)) != (string)required[i]["text"] ||
                        !(required[i]["children"] is JArray) || !walk(id, (JArray)required[i]["children"])) return false;
                }
                return true;
            };
            return walk((string)roots[0].Attribute("modelId"), expected) && edges.Count == visited.Count &&
                points.Where(p => p.Attribute("type") == null || (string)p.Attribute("type") == "node").All(p => visited.Contains((string)p.Attribute("modelId")));
        }

        private static bool CheckPlainTextExport(TaskDefinition task, out string detail)
        {
            string name = RequiredString(task, "expectedFileName");
            if (name != Path.GetFileName(name) || Path.IsPathRooted(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(Path.GetExtension(name), ".txt", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A single .txt export filename is required.");
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);
            detail = "The exact Documents export must be readable plain text containing the document's declared content.";
            if (!File.Exists(path)) return false;
            byte[] bytes;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (file.Length == 0 || file.Length > 16 * 1024 * 1024) return false;
                using (var copy = new MemoryStream()) { file.CopyTo(copy); bytes = copy.ToArray(); }
            }
            if ((bytes.Length > 1 && bytes[0] == 0x50 && bytes[1] == 0x4b) || (bytes.Length > 1 && bytes[0] == 0xd0 && bytes[1] == 0xcf)) return false;
            string text;
            try
            {
                if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
                else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
                else
                {
                    int skip = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                    try { text = new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip); }
                    catch (DecoderFallbackException) { if (skip != 0) return false; text = Encoding.Default.GetString(bytes); }
                }
            }
            catch (DecoderFallbackException) { return false; }
            if (text.TrimStart().StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("<?xml", StringComparison.Ordinal) ||
                text.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t' && c != '\f')) return false;
            string normalized = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            bool match = normalized.Length >= RequiredInt(task, "minimumCharacters") && RequiredStrings(task, "requiredTextSegments")
                .All(s => normalized.Contains(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim()));
            JToken alternatives;
            if (task.Extra.TryGetValue("textSegmentAlternatives", out alternatives))
                match &= ((JArray)alternatives).All(group => ((JArray)group).Values<string>().Any(s =>
                    normalized.Contains(System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim())));
            return match;
        }
    }
}
