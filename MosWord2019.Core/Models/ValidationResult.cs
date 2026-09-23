using System;
using System.Collections.Generic;
using System.Linq;

namespace MosWord2019.Core.Models
{
    public sealed class ValidationResult
    {
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();
        public ProjectMeta Meta { get; set; }
        public List<TaskDefinition> Tasks { get; set; }
        public Dictionary<string, Dictionary<string, string>> Languages { get; } =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        public bool HasErrors { get { return Issues.Any(issue => issue.Severity == ValidationSeverity.Error); } }
        public int ErrorCount { get { return Issues.Count(issue => issue.Severity == ValidationSeverity.Error); } }
        public int WarningCount { get { return Issues.Count(issue => issue.Severity == ValidationSeverity.Warning); } }
    }
}
