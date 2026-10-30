using System;
using System.IO;

namespace MosWord2019.Core.Models
{
    public sealed class OwnedWordProcessIdentity
    {
        public int ProcessId { get; }
        public long CreationFileTimeUtc { get; }
        public string ImagePath { get; }
        public bool IsWord => string.Equals(Path.GetFileName(ImagePath), "WINWORD.EXE", StringComparison.OrdinalIgnoreCase);
        public OwnedWordProcessIdentity(int processId, long creationFileTimeUtc, string imagePath)
        {
            if (processId <= 0 || creationFileTimeUtc <= 0 || string.IsNullOrWhiteSpace(imagePath) || !Path.IsPathRooted(imagePath))
                throw new ArgumentException("Invalid owned process identity.");
            DateTime.FromFileTimeUtc(creationFileTimeUtc);
            ProcessId = processId; CreationFileTimeUtc = creationFileTimeUtc; ImagePath = Path.GetFullPath(imagePath);
        }
        public bool Matches(OwnedWordProcessIdentity other) => other != null && IsWord && other.IsWord &&
            ProcessId == other.ProcessId && CreationFileTimeUtc == other.CreationFileTimeUtc &&
            string.Equals(ImagePath, other.ImagePath, StringComparison.OrdinalIgnoreCase);
    }
}
