using System;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    /// <summary>Task-declared external outputs, without saving Word or scanning user folders.</summary>
    public static class ExternalOutputLocation
    {
        public const string DocumentsAssertionType = "FileExistsInDocuments";
        public const string OwnedPlainTextAssertionType = "PlainTextAtOwnedSavedLocation";

        public static bool IsExistenceAssertion(string assertionType)
        {
            return assertionType == DocumentsAssertionType || assertionType == TemplateOutputLocation.AssertionType ||
                assertionType == OwnedPlainTextAssertionType;
        }

        public static bool IsOwnedPlainTextOutput(TrainingGradeContext context)
        {
            return context?.OwnedDocumentSaveFormat == 2;
        }

        public static bool CheckOwnedPlainText(TaskDefinition task, TrainingGradeContext context)
        {
            if (!IsOwnedPlainTextOutput(context) || string.IsNullOrWhiteSpace(context.OwnedDocumentPath)) return false;
            string name = (string)task.Extra?["expectedFileName"];
            // Validate the metadata as a single file name, without imposing a Documents destination.
            ResolveDocuments(name);
            string path = Path.GetFullPath(context.OwnedDocumentPath);
            if (!string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            // Reject renamed OOXML/OLE/RTF files. Encoding is deliberately not prescribed.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var prefix = new byte[8];
                int count = stream.Read(prefix, 0, prefix.Length);
                if (count >= 2 && prefix[0] == 0x50 && prefix[1] == 0x4B) return false;
                if (count >= 4 && prefix[0] == 0xD0 && prefix[1] == 0xCF && prefix[2] == 0x11 && prefix[3] == 0xE0) return false;
                if (count >= 5 && System.Text.Encoding.ASCII.GetString(prefix, 0, 5) == "{\\rtf") return false;
                return true;
            }
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
