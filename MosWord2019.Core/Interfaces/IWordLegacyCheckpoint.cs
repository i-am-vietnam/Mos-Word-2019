namespace MosWord2019.Core.Interfaces
{
    /// <summary>Opt-in preservation for native legacy Save As. Never cancels or converts the learner document.</summary>
    public interface IWordLegacyCheckpoint
    {
        void ConfigureLegacyCheckpoint(string checkpointPath);
        string GetPreservedFormattedPath();
    }
}
