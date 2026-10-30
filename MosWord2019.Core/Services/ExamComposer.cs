using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public sealed class ExamComposer
    {
        private readonly Candidate[] candidates;
        private const ulong ValidTotals = ((1UL << 41) - 1) ^ ((1UL << 34) - 1);

        // Prepare once for repeated seeds. No filesystem, Word, or mutable package state is retained.
        public ExamComposer(IEnumerable<TestingCatalogEntry> catalog, IEnumerable<ProjectPackage> packages)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (packages == null) throw new ArgumentNullException(nameof(packages));
            var byId = new Dictionary<string, ProjectPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages)
            {
                if (package?.Meta == null || string.IsNullOrWhiteSpace(package.Meta.ProjectId) || byId.ContainsKey(package.Meta.ProjectId))
                    throw new InvalidDataException("Invalid or duplicate exam package identity.");
                byId.Add(package.Meta.ProjectId, package);
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prepared = new List<Candidate>();
            foreach (var entry in catalog)
            {
                ProjectPackage package;
                if (entry == null || string.IsNullOrWhiteSpace(entry.ProjectId) || !seen.Add(entry.ProjectId) ||
                    !byId.TryGetValue(entry.ProjectId, out package))
                    throw new InvalidDataException("Unknown or duplicate exam catalog project.");
                var candidate = new Candidate(entry, package);
                if (entry.Eligible) prepared.Add(candidate);
            }
            if (seen.Count != byId.Count) throw new InvalidDataException("Every exam package requires a catalog entry.");
            candidates = prepared.OrderBy(c => c.ProjectId, StringComparer.Ordinal).ToArray();
            if (candidates.Length < 7 || !FeasibleSelections(0, new List<Candidate>()).Any())
                throw new InvalidDataException("Exam pool cannot form seven projects and 34-40 tasks.");
        }

        public static ExamDefinition Compose(IEnumerable<TestingCatalogEntry> catalog, IEnumerable<ProjectPackage> packages, long seed)
        {
            return new ExamComposer(catalog, packages).Compose(seed);
        }

        public ExamDefinition Compose(long seed)
        {
            var random = new ExamRandom(seed);
            Candidate[] selected = null;
            for (int attempt = 0; attempt < 4096; attempt++)
            {
                // Choose identities first, uniformly without replacement; task counts cannot weight projects.
                var shuffled = (Candidate[])candidates.Clone();
                random.Shuffle(shuffled);
                var proposal = shuffled.Take(7).ToArray();
                if (Compatible(proposal) && HasValidTotal(proposal)) { selected = proposal; break; }
            }
            if (selected == null)
            {
                // Rare restrictive catalogs: finite uniform reservoir over feasible project sets, not a retry loop.
                ulong seen = 0;
                foreach (var selection in FeasibleSelections(0, new List<Candidate>()))
                    if (random.Below(++seen) == 0) selected = selection;
                random.Shuffle(selected);
            }
            var ways = new ulong[8, 41];
            ways[7, 0] = 1;
            for (int i = 6; i >= 0; i--)
                for (int total = 0; total <= 40; total++)
                    foreach (int count in selected[i].Counts)
                        if (count <= total) ways[i, total] += ways[i + 1, total - count];
            int[] totals = Enumerable.Range(34, 7).Where(n => ways[0, n] != 0).ToArray();
            int remaining = totals[(int)random.Below((ulong)totals.Length)];
            var projects = new List<ExamProjectDefinition>();
            for (int i = 0; i < 7; i++)
            {
                ulong draw = random.Below(ways[i, remaining]);
                foreach (int count in selected[i].Counts)
                {
                    ulong weight = count <= remaining ? ways[i + 1, remaining - count] : 0;
                    if (draw < weight)
                    {
                        projects.Add(selected[i].Choose(count, random));
                        remaining -= count;
                        break;
                    }
                    draw -= weight;
                }
            }
            return new ExamDefinition(seed, projects);
        }

        private IEnumerable<Candidate[]> FeasibleSelections(int start, List<Candidate> chosen)
        {
            if (chosen.Count == 7)
            {
                if (HasValidTotal(chosen)) yield return chosen.ToArray();
                yield break;
            }
            for (int i = start; i <= candidates.Length - (7 - chosen.Count); i++)
            {
                chosen.Add(candidates[i]);
                if (Compatible(chosen))
                    foreach (var selection in FeasibleSelections(i + 1, chosen)) yield return selection;
                chosen.RemoveAt(chosen.Count - 1);
            }
        }

        private static bool Compatible(IEnumerable<Candidate> selected)
        {
            int legacy = 0;
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in selected)
                if ((candidate.Legacy && ++legacy > 1) || (candidate.ConflictGroup != null && !groups.Add(candidate.ConflictGroup))) return false;
            return true;
        }

        private static bool HasValidTotal(IEnumerable<Candidate> selected)
        {
            ulong totals = 1;
            foreach (var candidate in selected)
            {
                ulong next = 0;
                foreach (int count in candidate.Counts) next |= totals << count;
                totals = next;
            }
            return (totals & ValidTotals) != 0;
        }

        private sealed class Candidate
        {
            internal readonly string ProjectId;
            internal readonly string ConflictGroup;
            internal readonly bool Legacy;
            internal readonly int[] Counts;
            private readonly TaskDefinition[] tasks;
            private readonly HashSet<string> required;
            private readonly List<HashSet<string>> optional;
            private readonly ulong[,] ways;

            internal Candidate(TestingCatalogEntry entry, ProjectPackage package)
            {
                ProjectId = package.Meta.ProjectId;
                ConflictGroup = entry.ConflictGroup;
                Legacy = string.Equals(Path.GetExtension(package.Meta.Starter), ".doc", StringComparison.OrdinalIgnoreCase);
                if (package.Tasks == null || package.Tasks.Any(t => t == null || string.IsNullOrWhiteSpace(t.TaskId) || string.IsNullOrWhiteSpace(t.InstructionKey)) ||
                    package.Tasks.Select(t => t.TaskId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Tasks.Count ||
                    entry.RequiredTaskIds == null || entry.ExcludedTaskIds == null || entry.TaskGroups == null || entry.Notes == null ||
                    (ConflictGroup != null && string.IsNullOrWhiteSpace(ConflictGroup)))
                    throw new InvalidDataException("Invalid exam task metadata: " + ProjectId);
                tasks = package.Tasks.Select(t => new TaskDefinition { TaskId = t.TaskId, InstructionKey = t.InstructionKey }).ToArray();
                var ids = new HashSet<string>(tasks.Select(t => t.TaskId), StringComparer.OrdinalIgnoreCase);
                required = ReadIds(entry.RequiredTaskIds, ids);
                var excluded = ReadIds(entry.ExcludedTaskIds, ids);
                if (required.Overlaps(excluded)) throw new InvalidDataException("Required exam task is excluded.");
                var components = ids.ToDictionary(id => id, id => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }, StringComparer.OrdinalIgnoreCase);
                foreach (var group in entry.TaskGroups)
                {
                    var members = ReadIds(group, ids);
                    if (members.Count < 2 || members.Overlaps(excluded)) throw new InvalidDataException("Invalid exam task group.");
                    var merged = new HashSet<string>(members.SelectMany(id => components[id]), StringComparer.OrdinalIgnoreCase);
                    foreach (string id in merged) components[id] = merged;
                }
                var all = tasks.Where(t => !excluded.Contains(t.TaskId)).Select(t => components[t.TaskId]).Distinct().ToList();
                required = new HashSet<string>(all.Where(g => g.Overlaps(required)).SelectMany(g => g), StringComparer.OrdinalIgnoreCase);
                optional = all.Where(g => !g.Overlaps(required)).ToList();
                ways = new ulong[optional.Count + 1, 7];
                ways[optional.Count, 0] = 1;
                for (int i = optional.Count - 1; i >= 0; i--)
                    for (int n = 0; n <= 6; n++)
                        ways[i, n] = checked(ways[i + 1, n] + (n >= optional[i].Count ? ways[i + 1, n - optional[i].Count] : 0));
                Counts = Enumerable.Range(4, 3).Where(n => n >= required.Count && ways[0, n - required.Count] != 0).ToArray();
                if (entry.Eligible && Counts.Length == 0) throw new InvalidDataException("Exam project has no 4-6 task subset: " + ProjectId);
            }

            internal ExamProjectDefinition Choose(int count, ExamRandom random)
            {
                var selected = new HashSet<string>(required, StringComparer.OrdinalIgnoreCase);
                int need = count - required.Count;
                for (int i = 0; i < optional.Count; i++)
                {
                    int size = optional[i].Count;
                    ulong include = need >= size ? ways[i + 1, need - size] : 0;
                    if (random.Below(ways[i, need]) < include)
                    {
                        selected.UnionWith(optional[i]);
                        need -= size;
                    }
                }
                return new ExamProjectDefinition(ProjectId, tasks.Where(t => selected.Contains(t.TaskId)));
            }

            private static HashSet<string> ReadIds(List<string> values, HashSet<string> known)
            {
                if (values == null) throw new InvalidDataException("Exam task ID array is missing.");
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string value in values)
                    if (string.IsNullOrWhiteSpace(value) || !known.Contains(value) || !result.Add(value))
                        throw new InvalidDataException("Unknown or duplicate exam task ID.");
                return result;
            }
        }
    }
}
