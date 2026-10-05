using System;

namespace MosWord2019.Core.Interfaces
{
    /// <summary>Optional protection for a Training package that permits a lossy native Save As.</summary>
    public interface IWordExportCheckpoint
    {
        event Action<string> ExportCheckpointFailed;
        void ConfigureExportCheckpoint(string checkpointPath);
        string SaveFormattedCheckpoint();
    }
}
