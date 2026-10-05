using System;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    /// <summary>Task-declared existence checks, without saving Word or opening output content.</summary>
    public static class ExternalOutputLocation
    {
        public const string DocumentsAssertionType = "FileExistsInDocuments";

        public static bool IsExistenceAssertion(string assertionType)
        {
            return assertionType == DocumentsAssertionType || assertionType == TemplateOutputLocation.AssertionType;
        }

        public static string ResolveDocuments(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName) || fileName != Path.GetFileName(fileName) ||
                fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\") ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("A single Documents output file name is required.");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), fileName);
        }

        public static bool IsDeclaredDocumentsOutput(ProjectPackage package, string livePath)
        {
            if (package?.Tasks == null || string.IsNullOrWhiteSpace(livePath)) return false;
            return package.Tasks.Where(t => t.AssertionType == DocumentsAssertionType).Any(t =>
                string.Equals(Path.GetFullPath(livePath), ResolveDocuments((string)t.Extra["expectedFileName"]), StringComparison.OrdinalIgnoreCase));
        }
    }
}
