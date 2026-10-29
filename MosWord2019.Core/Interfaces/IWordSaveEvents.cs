using System;

namespace MosWord2019.Core.Interfaces
{
    /// <summary>Notifications from the verified owned document; no COM references leave Word.</summary>
    public interface IWordSaveEvents
    {
        event Action<string> OwnedDocumentSaving;
        event Action<string> WorkingFileWritten;
    }
}
