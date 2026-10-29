using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MosWord2019.Core.Models
{
    /// <summary>Plain file facts captured by Projects; Core never discovers external outputs.</summary>
    public sealed class OutputFileState
    {
        public OutputFileState(string path, bool exists, long length, string sha256,
            DateTime lastWriteUtc, string identity, DateTime observedUtc)
        {
            Path = path; Exists = exists; Length = length; Sha256 = sha256;
            LastWriteUtc = lastWriteUtc; Identity = identity; ObservedUtc = observedUtc;
        }

        public string Path { get; }
        public bool Exists { get; }
        public long Length { get; }
        public string Sha256 { get; }
        public DateTime LastWriteUtc { get; }
        public string Identity { get; }
        public DateTime ObservedUtc { get; }
    }

    public sealed class DeclaredOutputSnapshot
    {
        public DeclaredOutputSnapshot(OutputFileState baseline, OutputFileState current, string observationError = null)
        { Baseline = baseline; Current = current; ObservationError = observationError; }

        public OutputFileState Baseline { get; }
        public OutputFileState Current { get; }
        public string ObservationError { get; }
    }

    public sealed class OutputOwnershipSnapshot
    {
        public OutputOwnershipSnapshot(string sessionId, DateTime sessionStartedUtc, string documentsFolder,
            string templateFolder, IEnumerable<DeclaredOutputSnapshot> outputs)
        {
            SessionId = sessionId; SessionStartedUtc = sessionStartedUtc;
            DocumentsFolder = documentsFolder; TemplateFolder = templateFolder;
            Outputs = new ReadOnlyCollection<DeclaredOutputSnapshot>((outputs ?? Enumerable.Empty<DeclaredOutputSnapshot>()).ToList());
        }

        public string SessionId { get; }
        public DateTime SessionStartedUtc { get; }
        public string DocumentsFolder { get; }
        public string TemplateFolder { get; }
        public IReadOnlyList<DeclaredOutputSnapshot> Outputs { get; }
    }
}
