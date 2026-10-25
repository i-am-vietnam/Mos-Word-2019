using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MosWord2019.Core.Models
{
    public sealed class WordTocTargetSnapshot
    {
        public string Bookmark { get; private set; }
        public string Text { get; private set; }
        public int AdjustedPageNumber { get; private set; }
        public WordTocTargetSnapshot(string bookmark, string text, int adjustedPageNumber)
        { Bookmark = bookmark; Text = text; AdjustedPageNumber = adjustedPageNumber; }
    }

    public sealed class WordTocLayoutSnapshot
    {
        public string DocumentPath { get; private set; }
        public IReadOnlyList<WordTocTargetSnapshot> Targets { get; private set; }
        public WordTocLayoutSnapshot(string documentPath, IEnumerable<WordTocTargetSnapshot> targets)
        {
            DocumentPath = documentPath;
            Targets = new ReadOnlyCollection<WordTocTargetSnapshot>((targets ?? Enumerable.Empty<WordTocTargetSnapshot>()).ToList());
        }
    }
}
