using System;
using System.IO;

namespace MosWord2019.Core.Services
{
    /// <summary>Exact task-declared output names; no directory traversal or arbitrary cleanup paths.</summary>
    public static class TemplateOutputLocation
    {
        public const string AssertionType = "FileExistsInCustomOfficeTemplates";

        public static string Resolve(string fileName)
        {
            return Resolve(fileName, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }

        internal static string Resolve(string fileName, string documentsRoot)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(Path.GetExtension(fileName), ".dotx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A single .dotx training output file name is required.");
            return Path.Combine(documentsRoot, "Custom Office Templates", fileName);
        }
    }
}
