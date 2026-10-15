namespace MosWord2019.Core.Models
{
    /// <summary>Read-only flags observed from the owned Word window; null means unavailable.</summary>
    public sealed class WordFormattingMarksSnapshot
    {
        public const string AssertionType = "FormattingMarksVisible";
        public bool? Tabs { get; }
        public bool? Spaces { get; }
        public bool? Paragraphs { get; }
        public bool? HiddenText { get; }
        public bool? OptionalHyphens { get; }
        public bool? ObjectAnchors { get; }
        public bool? ShowAll { get; }

        public WordFormattingMarksSnapshot(bool? tabs, bool? spaces, bool? paragraphs,
            bool? hiddenText, bool? optionalHyphens, bool? objectAnchors, bool? showAll)
        {
            Tabs = tabs; Spaces = spaces; Paragraphs = paragraphs; HiddenText = hiddenText;
            OptionalHyphens = optionalHyphens; ObjectAnchors = objectAnchors; ShowAll = showAll;
        }
    }
}
