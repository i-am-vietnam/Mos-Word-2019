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
            return ResolveInFolder(fileName, TemplateFolderResolver.Resolve());
        }

        internal static string Resolve(string fileName, string documentsRoot)
        {
            return ResolveInFolder(fileName, TemplateFolderResolver.Resolve("16.0", _ => null, documentsRoot));
        }

        public static string ResolveInFolder(string fileName, string templateFolder)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(Path.GetExtension(fileName), ".dotx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A single .dotx training output file name is required.");
            return Path.Combine(TemplateFolderResolver.NormalizeFolder(templateFolder), fileName);
        }
    }
}
