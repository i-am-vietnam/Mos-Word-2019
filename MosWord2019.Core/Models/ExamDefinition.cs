using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MosWord2019.Core.Models
{
    public sealed class ExamProjectDefinition
    {
        public string ProjectId { get; }
        public IReadOnlyList<string> TaskIds { get; }
        public IReadOnlyDictionary<string, string> InstructionKeys { get; }

        public ExamProjectDefinition(string projectId, IEnumerable<TaskDefinition> tasks)
        {
            if (projectId == null || !Regex.IsMatch(projectId, @"^Word2019_P[0-9]{2,}\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid exam project ID.");
            var selected = (tasks ?? throw new ArgumentNullException(nameof(tasks))).ToList();
            if (selected.Count < 4 || selected.Count > 6 || selected.Any(t => t == null ||
                string.IsNullOrWhiteSpace(t.TaskId) || string.IsNullOrWhiteSpace(t.InstructionKey)) ||
                selected.Select(t => t.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
                throw new InvalidDataException("An exam project requires 4-6 unique tasks with instruction keys.");
            ProjectId = projectId;
            TaskIds = selected.Select(t => t.TaskId).ToList().AsReadOnly();
            InstructionKeys = new ReadOnlyDictionary<string, string>(selected.ToDictionary(t => t.TaskId, t => t.InstructionKey));
        }
    }

    public sealed class ExamDefinition
    {
        public long Seed { get; }
        public IReadOnlyList<ExamProjectDefinition> Projects { get; }
        public int N { get; }

        public ExamDefinition(long seed, IEnumerable<ExamProjectDefinition> projects)
        {
            var selected = (projects ?? throw new ArgumentNullException(nameof(projects))).ToList();
            if (selected.Count != 7 || selected.Any(p => p == null) ||
                selected.Select(p => p.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 7)
                throw new InvalidDataException("An exam requires exactly seven distinct projects.");
            int count = selected.Sum(p => p.TaskIds.Count);
            if (count < 34 || count > 40) throw new InvalidDataException("An exam requires 34-40 tasks.");
            Seed = seed;
            Projects = selected.AsReadOnly();
            N = count;
        }
    }
}
