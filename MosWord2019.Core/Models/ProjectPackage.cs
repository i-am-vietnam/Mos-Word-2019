using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MosWord2019.Core.Models
{
    public sealed class ProjectPackage
    {
        private static readonly Regex ProjectIdPattern =
            new Regex(@"^Word2019_P(?<number>\d{2,})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public ProjectMeta Meta { get; set; } = new ProjectMeta();
        public List<TaskDefinition> Tasks { get; set; } = new List<TaskDefinition>();
        public Dictionary<string, string> Lang { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string ProjectFolderPath { get; set; } = "";

        public string DisplayName
        {
            get
            {
                string projectId = Meta == null ? "" : Meta.ProjectId;
                Match match = ProjectIdPattern.Match(projectId ?? "");
                int number;
                return match.Success && int.TryParse(match.Groups["number"].Value, out number)
                    ? "Project " + number
                    : string.IsNullOrWhiteSpace(projectId) ? "Project" : projectId;
            }
        }
    }
}
