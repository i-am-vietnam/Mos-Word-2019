using System;
using System.Collections.Generic;
using System.IO;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;

namespace MosWord2019.Projects
{
    /// <summary>Call only after the owned Word document/application has closed successfully.</summary>
    public sealed class TemplateOutputCleanupService
    {
        private readonly HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Register(ProjectPackage package)
        {
            foreach (var task in package.Tasks)
            {
                if (task.AssertionType != TemplateOutputLocation.AssertionType) continue;
                Newtonsoft.Json.Linq.JToken name;
                if (task.Extra == null || !task.Extra.TryGetValue("expectedFileName", out name))
                    throw new InvalidDataException("Missing template output name.");
                targets.Add(TemplateOutputLocation.Resolve((string)name));
            }
        }

        public void Cleanup()
        {
            foreach (string path in targets)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    if ((File.GetAttributes(Path.GetDirectoryName(path)) & FileAttributes.ReparsePoint) != 0 ||
                        (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new IOException("Refusing to clean a redirected template output.");
                    File.Delete(path); // Exact declared file only; never recursive.
                    AppLogger.Write("Training template output removed " + path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    AppLogger.Write("Training template output cleanup failed " + path, ex);
                }
            }
        }
    }
}
