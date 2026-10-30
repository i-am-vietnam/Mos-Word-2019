using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace MosWord2019.Core.Models
{
    /// <summary>Plain file facts captured by Projects; Core never discovers external outputs.</summary>
    public sealed class OutputFileState
    {
        private static long nextObservationSequence;

        public OutputFileState(string path, bool exists, long length, string sha256,
            DateTime lastWriteUtc, string identity, DateTime observedUtc,
            long? observationSequence = null, DateTime creationUtc = default(DateTime))
        {
            Path = path; Exists = exists; Length = length; Sha256 = sha256;
            LastWriteUtc = lastWriteUtc; Identity = identity; ObservedUtc = observedUtc;
            if (observationSequence.HasValue)
            {
                long seen;
                do { seen = Interlocked.Read(ref nextObservationSequence); if (seen >= observationSequence.Value) break; }
                while (Interlocked.CompareExchange(ref nextObservationSequence, observationSequence.Value, seen) != seen);
            }
            ObservationSequence = observationSequence ?? Interlocked.Increment(ref nextObservationSequence);
            CreatedUtc = creationUtc;
        }

        public string Path { get; }
        public bool Exists { get; }
        public long Length { get; }
        public string Sha256 { get; }
        public DateTime LastWriteUtc { get; }
        public string Identity { get; }
        public DateTime ObservedUtc { get; }
        public long ObservationSequence { get; }
        public DateTime CreatedUtc { get; }
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
