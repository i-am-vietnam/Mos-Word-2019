using System;
using System.Collections.Generic;
using MosWord2019.Core.Models;
using Wd = Microsoft.Office.Interop.Word;

namespace MosWord2019.Word
{
    public sealed partial class WordController
    {
        public WordTocLayoutSnapshot ReadTocLayout()
        {
            EnsureUsable();
            if (!IsOpened) throw new InvalidOperationException("No owned document is open for TOC layout inspection.");
            var targets = new List<WordTocTargetSnapshot>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            Wd.TablesOfContents tables = null; Wd.Bookmarks bookmarks = null;
            try
            {
                tables = session.Document.TablesOfContents;
                bookmarks = session.Document.Bookmarks;
                for (int i = 1; i <= tables.Count; i++)
                {
                    Wd.TableOfContents table = null; Wd.Range range = null; Wd.Hyperlinks links = null;
                    try
                    {
                        table = tables[i]; range = table.Range; links = range.Hyperlinks;
                        for (int j = 1; j <= links.Count; j++)
                        {
                            Wd.Hyperlink link = null; Wd.Bookmark bookmark = null; Wd.Range target = null;
                            try
                            {
                                link = links[j]; string name = link.SubAddress;
                                if (string.IsNullOrEmpty(name) || !names.Add(name)) continue;
                                if (!bookmarks.Exists(name)) { targets.Add(new WordTocTargetSnapshot(name, null, 0)); continue; }
                                object key = name; bookmark = bookmarks.get_Item(ref key); target = bookmark.Range;
                                string text = target.Text?.TrimEnd('\r', '\a');
                                target.Collapse(Wd.WdCollapseDirection.wdCollapseStart);
                                int page = Convert.ToInt32((object)target.Information[Wd.WdInformation.wdActiveEndAdjustedPageNumber]);
                                if (page < 1) throw new InvalidOperationException("Word did not supply the target's current adjusted page number.");
                                targets.Add(new WordTocTargetSnapshot(name, text, page));
                            }
                            finally { ReleaseCom(target); ReleaseCom(bookmark); ReleaseCom(link); }
                        }
                    }
                    finally { ReleaseCom(links); ReleaseCom(range); ReleaseCom(table); }
                }
                return new WordTocLayoutSnapshot(session.Document.FullName, targets);
            }
            finally { ReleaseCom(bookmarks); ReleaseCom(tables); }
        }
    }
}
