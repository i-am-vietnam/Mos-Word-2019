namespace MosWord2019.Core.Interfaces
{
    /// <summary>Optional live document state. Does not expose Office COM objects or start another application.</summary>
    public interface IWordDocumentState
    {
        string CurrentDocumentPath { get; }
        string DefaultTemplateFolder { get; }
    }
}
