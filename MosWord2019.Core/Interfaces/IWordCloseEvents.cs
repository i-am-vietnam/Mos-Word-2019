using System;
namespace MosWord2019.Core.Interfaces
{
    public interface IWordInputLock { void SetInputEnabled(bool enabled); }
    /// <summary>Plain owned-document facts; the native checkpoint is complete before notification.</summary>
    public interface IWordCloseEvents
    {
        event Action<string, int, string> OwnedDocumentClosing;
    }
}
