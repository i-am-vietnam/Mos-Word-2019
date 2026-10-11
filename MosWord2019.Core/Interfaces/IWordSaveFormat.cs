namespace MosWord2019.Core.Interfaces
{
    /// <summary>Native saved format of the already-owned document; never starts Office.</summary>
    public interface IWordSaveFormat
    {
        int CurrentSaveFormat { get; }
    }
}
