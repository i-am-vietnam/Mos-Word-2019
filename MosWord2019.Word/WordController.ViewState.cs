using System;
using MosWord2019.Core.Models;
using Wd = Microsoft.Office.Interop.Word;

namespace MosWord2019.Word
{
    public sealed partial class WordController
    {
        public WordFormattingMarksSnapshot ReadFormattingMarks()
        {
            EnsureUsable();
            if (!IsOpened) throw new InvalidOperationException("No owned Word document is open for view inspection.");
            Wd.Window window = null;
            Wd.View view = null;
            try
            {
                window = session.Document.ActiveWindow;
                view = window.View;
                return new WordFormattingMarksSnapshot(view.ShowTabs, view.ShowSpaces, view.ShowParagraphs,
                    view.ShowHiddenText, view.ShowHyphens, view.ShowObjectAnchors, view.ShowAll);
            }
            finally { ReleaseCom(view); ReleaseCom(window); }
        }
    }
}
