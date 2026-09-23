using System;

namespace MosWord2019.Core.Interfaces
{
    /// <summary>Single-document lifecycle, used and disposed on its creating STA thread.</summary>
    public interface IWordController : IDisposable
    {
        bool IsOpened { get; }
        void StartWord();
        /// <summary>Open an existing writable .docx working copy, never starter.docx.</summary>
        void OpenDocument(string filePath);
        void Save();
        /// <summary>Close the owned document without saving. Call Save explicitly first.</summary>
        void CloseDocument();
        /// <summary>Best-effort discard/quit/release. Reports unexpected cleanup failures.</summary>
        void Close();
    }
}
