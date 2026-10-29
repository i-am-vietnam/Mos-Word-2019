using System;
using System.IO;
using Microsoft.Win32;

namespace MosWord2019.Core.Services
{
    /// <summary>Word's PersonalTemplates setting, independent of Normal.dotm/UserTemplatesPath.</summary>
    public static class TemplateFolderResolver
    {
        public static string Resolve(string officeVersion = "16.0")
        {
            return Resolve(officeVersion, ReadPersonalTemplates,
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }

        internal static string Resolve(string officeVersion, Func<string, string> personalTemplatesReader, string documentsRoot)
        {
            if (personalTemplatesReader == null) throw new ArgumentNullException(nameof(personalTemplatesReader));
            Version version;
            if (!Version.TryParse(officeVersion, out version))
                throw new ArgumentException("A valid Office version is required.", nameof(officeVersion));
            string configured = personalTemplatesReader(@"Software\Microsoft\Office\" + officeVersion + @"\Word\Options");
            return NormalizeFolder(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(documentsRoot, "Custom Office Templates") : configured);
        }

        internal static string NormalizeFolder(string folder)
        {
            // Invalid settings fail closed: never interpret a relative setting against the process directory.
            if (string.IsNullOrWhiteSpace(folder) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new InvalidDataException("An absolute template folder is required.");
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(folder.Trim());
                string root = Path.GetPathRoot(expanded);
                if (string.IsNullOrEmpty(root) || root == @"\" || root.EndsWith(":", StringComparison.Ordinal))
                    throw new InvalidDataException("An absolute template folder is required.");
                string fullPath = Path.GetFullPath(expanded);
                return fullPath.Length == root.Length ? fullPath : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new InvalidDataException("The template folder setting is invalid.", ex);
            }
        }

        private static string ReadPersonalTemplates(string subKey)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey))
                return key?.GetValue("PersonalTemplates", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
    }
}
