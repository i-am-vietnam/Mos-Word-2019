using Wd = Microsoft.Office.Interop.Word;

namespace MosWord2019.Word
{
    // State only; WordController owns all transitions and COM release.
    internal sealed class WordSession
    {
        internal Wd.Application Application;
        internal Wd.Document Document;
        internal WinApiProcessHelper.OwnedProcess Process;
        internal string DocumentPath;
    }
}
