using MosWord2019.Core.Models;
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

        public ProjectLoader() : this(new ProjectValidator()) { }

        internal ProjectLoader(ProjectValidator validator)
        {
            this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        public List<ProjectPackage> LoadAll(string projectsRootPath, string languageCode)
        {
            if (string.IsNullOrWhiteSpace(projectsRootPath) || !Directory.Exists(projectsRootPath))
                return new List<ProjectPackage>();

            return Directory.GetDirectories(projectsRootPath, "Word2019_P*", SearchOption.TopDirectoryOnly)
                .Where(path => FolderPattern.IsMatch(new DirectoryInfo(path).Name))
                .Select(path => Load(path, languageCode))
                .Where(package => package != null)
                .OrderBy(package => package.Meta.ProjectId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public ProjectPackage Load(string projectFolderPath, string languageCode)
        {
            ValidationResult validation = validator.Validate(projectFolderPath, languageCode);
            if (validation.HasErrors) return null;

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
