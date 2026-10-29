using MosWord2019.Core.Models;
using MosWord2019.Core.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MosWord2019.Projects
{
    public sealed class ProjectLoader
    {
        private static readonly Regex FolderPattern =
            new Regex(@"^Word2019_P\d{2,}$", RegexOptions.CultureInvariant);
        private readonly ProjectValidator validator;
        private readonly Action<string> log;

        public int LastRejectedCount { get; private set; }

        public ProjectLoader() : this(new ProjectValidator()) { }

        internal ProjectLoader(ProjectValidator validator)
            : this(validator, message => AppLogger.Write(message)) { }

        internal ProjectLoader(ProjectValidator validator, Action<string> log)
        {
            this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public List<ProjectPackage> LoadAll(string projectsRootPath, string languageCode)
        {
            LastRejectedCount = 0;
            if (string.IsNullOrWhiteSpace(projectsRootPath) || !Directory.Exists(projectsRootPath))
                return new List<ProjectPackage>();

            var packages = new List<ProjectPackage>();
            foreach (string path in Directory.GetDirectories(projectsRootPath, "Word2019_P*", SearchOption.TopDirectoryOnly)
                .Where(path => FolderPattern.IsMatch(new DirectoryInfo(path).Name)))
            {
                ValidationResult validation = validator.Validate(path, languageCode);
                if (validation.HasErrors)
                {
                    LastRejectedCount++;
                    log("Project package rejected " + path + ": " + string.Join("; ",
                        validation.Issues.Select(issue => issue.Code + " [" + issue.ProjectId + "/" + issue.TaskId + "] " + issue.Message)));
                    continue;
                }
                packages.Add(CreatePackage(path, languageCode, validation));
            }
            return packages.OrderBy(package => package.Meta.ProjectId, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public ProjectPackage Load(string projectFolderPath, string languageCode)
        {
            ValidationResult validation = validator.Validate(projectFolderPath, languageCode);
            if (validation.HasErrors) return null;
            return CreatePackage(projectFolderPath, languageCode, validation);
        }

        private static ProjectPackage CreatePackage(string projectFolderPath, string languageCode, ValidationResult validation)
        {
            string language = languageCode.Trim().ToLowerInvariant();
            return new ProjectPackage
            {
                Meta = validation.Meta,
                Tasks = validation.Tasks ?? new List<TaskDefinition>(),
                Lang = validation.Languages[language],
                ProjectFolderPath = Path.GetFullPath(projectFolderPath)
            };
        }

        public string GetText(ProjectPackage package, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            string value;
            return package != null && package.Lang != null && package.Lang.TryGetValue(key, out value)
                ? value
                : key;
        }
    }
}
