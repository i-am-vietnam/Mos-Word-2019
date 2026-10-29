using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Projects
{
    public sealed class TestingCatalogValidator
    {
        private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

        public ValidationResult Validate(IEnumerable<TestingCatalogEntry> entries, IEnumerable<ProjectPackage> packages)
        {
            var result = new ValidationResult();
            var byId = new Dictionary<string, ProjectPackage>(IdComparer);
            foreach (var package in packages ?? Enumerable.Empty<ProjectPackage>())
            {
                string id = package == null || package.Meta == null ? null : package.Meta.ProjectId;
                if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id))
                    Error(result, "CATALOG_PACKAGE_INVALID", id, "Package identities must be nonempty and unique.");
                else byId.Add(id, package);
            }
            var seen = new HashSet<string>(IdComparer);
            var eligible = new List<Tuple<TestingCatalogEntry, ProjectPackage, IReadOnlyList<int>>>();
            foreach (var entry in entries ?? Enumerable.Empty<TestingCatalogEntry>())
            {
                string id = entry == null ? null : entry.ProjectId;
                if (string.IsNullOrWhiteSpace(id))
                {
                    Error(result, "CATALOG_PROJECT_INVALID", id, "Catalog entry requires a projectId.");
                    continue;
                }
                if (!seen.Add(id)) Error(result, "CATALOG_PROJECT_DUPLICATE", id, "Duplicate catalog project.");
                ProjectPackage package;
                if (!byId.TryGetValue(id, out package))
                {
                    Error(result, "CATALOG_PROJECT_UNKNOWN", id, "Unknown catalog project.");
                    continue;
                }
                int previousErrors = result.ErrorCount;
                var counts = Analyze(entry, package, result);
                if (entry.Eligible && previousErrors == result.ErrorCount)
                    eligible.Add(Tuple.Create(entry, package, counts));
            }
            foreach (string id in byId.Keys.Where(id => !seen.Contains(id)))
                Error(result, "CATALOG_PROJECT_MISSING", id, "Package has no catalog entry.");
            if (!PoolFeasible(eligible))
                Error(result, "CATALOG_POOL_INFEASIBLE", "", "No selection of exactly 7 projects and 34-40 tasks satisfies conflict and legacy limits.");
            return result;
        }

        public IReadOnlyList<int> GetSelectableTaskCounts(TestingCatalogEntry entry, ProjectPackage package)
        {
            var result = new ValidationResult();
            var counts = Analyze(entry, package, result);
            return result.HasErrors ? new List<int>() : counts;
        }

        private static IReadOnlyList<int> Analyze(TestingCatalogEntry entry, ProjectPackage package, ValidationResult result)
        {
            string id = entry == null ? "" : entry.ProjectId;
            int errorsBefore = result.ErrorCount;
            if (entry == null || package == null || package.Tasks == null)
            {
                Error(result, "CATALOG_TASK_INVALID", id, "Entry, package and tasks must be present.");
                return new List<int>();
            }
            if (entry.Notes == null || (entry.ConflictGroup != null && string.IsNullOrWhiteSpace(entry.ConflictGroup)))
                Error(result, "CATALOG_METADATA_INVALID", id, "Notes must be a string; conflictGroup must be null or nonempty.");
            var tasks = new HashSet<string>(IdComparer);
            foreach (var task in package.Tasks)
                if (task == null || string.IsNullOrWhiteSpace(task.TaskId) || !tasks.Add(task.TaskId))
                    Error(result, "CATALOG_TASK_INVALID", id, "Package tasks require unique nonempty IDs.");
            var required = ReadTaskIds(entry.RequiredTaskIds, tasks, result, id);
            var excluded = ReadTaskIds(entry.ExcludedTaskIds, tasks, result, id);
            if (required.Overlaps(excluded))
                Error(result, "CATALOG_REQUIRED_EXCLUDED", id, "Required tasks cannot be excluded.");
            var parent = tasks.ToDictionary(t => t, t => t, IdComparer);
            if (entry.TaskGroups == null)
                Error(result, "CATALOG_GROUP_INVALID", id, "taskGroups must be an array.");
            else foreach (var group in entry.TaskGroups)
            {
                var members = ReadTaskIds(group, tasks, result, id);
                if (group == null || group.Count < 2)
                    Error(result, "CATALOG_GROUP_INVALID", id, "Each task group needs at least two members.");
                if (members.Overlaps(excluded))
                    Error(result, "CATALOG_GROUP_EXCLUDED", id, "Grouped tasks cannot be excluded.");
                var known = members.Where(tasks.Contains).ToList();
                for (int i = 1; i < known.Count; i++) parent[Root(parent, known[i])] = Root(parent, known[0]);
            }
            int selectable = tasks.Count(t => !excluded.Contains(t));
            if (entry.Eligible && selectable < 4)
                Error(result, "CATALOG_SELECTABLE_TOO_FEW", id, "Eligible project needs at least four selectable tasks.");
            if (result.ErrorCount != errorsBefore) return new List<int>();
            var components = tasks.Where(t => !excluded.Contains(t)).GroupBy(t => Root(parent, t)).ToList();
            int mandatory = components.Where(c => c.Any(required.Contains)).Sum(c => c.Count());
            var totals = new HashSet<int> { mandatory };
            foreach (var component in components.Where(c => !c.Any(required.Contains)))
            {
                int size = component.Count();
                foreach (int count in totals.ToArray()) if (count + size <= 6) totals.Add(count + size);
            }
            var valid = totals.Where(t => t >= 4 && t <= 6).OrderBy(t => t).ToList();
            if (entry.Eligible && valid.Count == 0)
                Error(result, "CATALOG_PROJECT_INFEASIBLE", id, "Required tasks and groups cannot form a 4-6 task subset.");
            return valid;
        }

        private static HashSet<string> ReadTaskIds(List<string> ids, HashSet<string> tasks, ValidationResult result, string projectId)
        {
            var seen = new HashSet<string>(IdComparer);
            if (ids == null) Error(result, "CATALOG_TASK_INVALID", projectId, "Task ID arrays must be present.");
            else foreach (string id in ids)
            {
                if (string.IsNullOrWhiteSpace(id)) Error(result, "CATALOG_TASK_INVALID", projectId, "Task IDs must be nonempty strings.");
                else
                {
                    if (!seen.Add(id)) Error(result, "CATALOG_TASK_DUPLICATE", projectId, "Duplicate task ID: " + id);
                    if (!tasks.Contains(id)) Error(result, "CATALOG_TASK_UNKNOWN", projectId, "Unknown task ID: " + id);
                }
            }
            return seen;
        }

        private static string Root(Dictionary<string, string> parent, string task)
        {
            while (!IdComparer.Equals(parent[task], task)) task = parent[task];
            return task;
        }

        private static bool PoolFeasible(List<Tuple<TestingCatalogEntry, ProjectPackage, IReadOnlyList<int>>> projects)
        {
            var buckets = new List<List<Tuple<TestingCatalogEntry, ProjectPackage, IReadOnlyList<int>>>>();
            var conflicts = new Dictionary<string, List<Tuple<TestingCatalogEntry, ProjectPackage, IReadOnlyList<int>>>>(IdComparer);
            foreach (var project in projects)
            {
                if (project.Item1.ConflictGroup == null) buckets.Add(new List<Tuple<TestingCatalogEntry, ProjectPackage, IReadOnlyList<int>>> { project });
                else
                {
                    List<Tuple<TestingCatalogEntry, ProjectPackage, IReadOnlyList<int>>> bucket;
                    if (!conflicts.TryGetValue(project.Item1.ConflictGroup, out bucket))
                    {
                        bucket = new List<Tuple<TestingCatalogEntry, ProjectPackage, IReadOnlyList<int>>>();
                        conflicts.Add(project.Item1.ConflictGroup, bucket);
                        buckets.Add(bucket);
                    }
                    bucket.Add(project);
                }
            }
            var states = new HashSet<Tuple<int, int, int>> { Tuple.Create(0, 0, 0) };
            foreach (var bucket in buckets)
            {
                var next = new HashSet<Tuple<int, int, int>>(states);
                foreach (var state in states)
                foreach (var project in bucket)
                foreach (int count in project.Item3)
                {
                    int legacy = state.Item3 + (string.Equals(Path.GetExtension(project.Item2.Meta.Starter), ".doc", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                    if (state.Item1 < 7 && state.Item2 + count <= 40 && legacy <= 1)
                        next.Add(Tuple.Create(state.Item1 + 1, state.Item2 + count, legacy));
                }
                states = next;
            }
            return states.Any(s => s.Item1 == 7 && s.Item2 >= 34 && s.Item2 <= 40);
        }

        private static void Error(ValidationResult result, string code, string projectId, string message)
        {
            result.Issues.Add(new ValidationIssue { Severity = ValidationSeverity.Error, Code = code, ProjectId = projectId ?? "", Message = message });
        }
    }
}
