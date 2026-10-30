using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class ExamTests
    {
        [TestMethod]
        public void OneHundredThousandSeedsRespectEveryCatalogRuleAndProjectFrequency()
        {
            var packages = Packages();
            var catalog = Catalog(packages);
            var composer = new ExamComposer(catalog, packages);
            var frequencies = catalog.Where(e => e.Eligible).ToDictionary(e => e.ProjectId, e => 0);
            var byId = packages.ToDictionary(p => p.Meta.ProjectId);
            var entries = catalog.ToDictionary(e => e.ProjectId);
            var totals = new HashSet<int>();
            for (int seed = 0; seed < 100000; seed++)
            {
                var exam = composer.Compose(seed);
                Assert.AreEqual(seed, exam.Seed);
                Assert.AreEqual(7, exam.Projects.Count);
                Assert.AreEqual(7, exam.Projects.Select(p => p.ProjectId).Distinct().Count());
                Assert.IsTrue(exam.N >= 34 && exam.N <= 40);
                totals.Add(exam.N);
                var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int legacy = 0;
                foreach (var chosen in exam.Projects)
                {
                    var entry = entries[chosen.ProjectId];
                    var package = byId[chosen.ProjectId];
                    Assert.IsTrue(entry.Eligible);
                    Assert.IsTrue(chosen.TaskIds.Count >= 4 && chosen.TaskIds.Count <= 6);
                    CollectionAssert.AreEqual(package.Tasks.Where(t => chosen.TaskIds.Contains(t.TaskId)).Select(t => t.TaskId).ToList(), chosen.TaskIds.ToList());
                    Assert.IsTrue(entry.RequiredTaskIds.All(chosen.TaskIds.Contains));
                    Assert.IsFalse(entry.ExcludedTaskIds.Any(chosen.TaskIds.Contains));
                    foreach (var group in entry.TaskGroups)
                        Assert.IsTrue(group.All(chosen.TaskIds.Contains) || !group.Any(chosen.TaskIds.Contains));
                    if (entry.ConflictGroup != null) Assert.IsTrue(conflicts.Add(entry.ConflictGroup));
                    if (Path.GetExtension(package.Meta.Starter) == ".doc") legacy++;
                    foreach (var id in chosen.TaskIds)
                        Assert.AreEqual(package.Tasks.Single(t => t.TaskId == id).InstructionKey, chosen.InstructionKeys[id]);
                    frequencies[chosen.ProjectId]++;
                }
                Assert.IsTrue(legacy <= 1);
                int[] points = ScoreAllocator.Allocate(exam.N, seed);
                Assert.AreEqual(1000, points.Sum());
                Assert.IsTrue(points.All(p => p >= 25 && p <= 30));
            }
            CollectionAssert.AreEquivalent(Enumerable.Range(34, 7).ToArray(), totals.ToArray());
            double expected = 700000.0 / frequencies.Count;
            foreach (var pair in frequencies)
            {
                Console.WriteLine(pair.Key + "=" + pair.Value);
                Assert.IsTrue(pair.Value >= expected * .8 && pair.Value <= expected * 1.2,
                    pair.Key + " frequency " + pair.Value + "; uniform target " + expected);
            }
        }

        [TestMethod]
        public void SeedIsStableAcrossInputOrderAndRepeatedCalls()
        {
            var packages = Packages();
            var catalog = Catalog(packages);
            var composer = new ExamComposer(catalog, packages);
            var reordered = new ExamComposer(catalog.Reverse(), packages.AsEnumerable().Reverse());
            foreach (long seed in new[] { 0L, 1L, -1L, long.MinValue, long.MaxValue })
            {
                string expected = Fingerprint(composer.Compose(seed));
                Assert.AreEqual(expected, Fingerprint(composer.Compose(seed)));
                Assert.AreEqual(expected, Fingerprint(reordered.Compose(seed)));
                Assert.AreEqual(expected, Fingerprint(ExamComposer.Compose(catalog, packages, seed)));
                CollectionAssert.AreEqual(ScoreAllocator.Allocate(37, seed), ScoreAllocator.Allocate(37, seed));
            }
        }

        [DataTestMethod]
        [DataRow(34, 29, 14)]
        [DataRow(35, 28, 20)]
        [DataRow(36, 27, 28)]
        [DataRow(37, 27, 1)]
        [DataRow(38, 26, 12)]
        [DataRow(39, 25, 25)]
        [DataRow(40, 25, 0)]
        public void ScoreAllocationMatchesExactBaseAndRemainder(int count, int basePoints, int remainder)
        {
            var points = ScoreAllocator.Allocate(count, 12345);
            Assert.AreEqual(count, points.Length);
            Assert.AreEqual(1000, points.Sum());
            Assert.AreEqual(remainder, points.Count(p => p == basePoints + 1));
            Assert.AreEqual(count - remainder, points.Count(p => p == basePoints));
            Assert.IsTrue(points.All(p => p >= 25 && p <= 30));
        }

        [DataTestMethod]
        [DataRow(33)]
        [DataRow(41)]
        public void AllocationRejectsInvalidTaskCount(int count)
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => ScoreAllocator.Allocate(count, 0));
        }

        [DataTestMethod]
        [DataRow(699, false)]
        [DataRow(700, true)]
        [DataRow(1000, true)]
        [DataRow(0, false)]
        public void ScorerUsesExactThresholdAndHidesDiagnostics(int target, bool passed)
        {
            var packages = Packages();
            var composer = new ExamComposer(Catalog(packages), packages);
            // N=36 has enough 27/28-point tasks to realize both 699 and 700 exactly.
            var exam = Enumerable.Range(0, 100).Select(seed => composer.Compose(seed)).First(e => e.N == 36);
            var selected = CorrectSubset(ScoreAllocator.Allocate(exam.N, exam.Seed), target);
            var grades = new List<ExamTaskGrade>();
            var log = new List<string>();
            int index = 0;
            foreach (var project in exam.Projects)
                foreach (var task in project.TaskIds)
                {
                    bool correct = selected.Contains(index++);
                    grades.Add(new ExamTaskGrade(project.ProjectId, task,
                        new TaskGradeResult(correct ? TaskGradeOutcome.Pass : TaskGradeOutcome.Error,
                            "PRIVATE grader diagnostic", "test", task)));
                }
            var result = new ExamScorer(log.Add).Score(exam, grades);
            Assert.AreEqual(target, result.Total);
            Assert.AreEqual(passed, result.Passed);
            Assert.AreEqual(target, result.PerProject.Sum(p => p.Points));
            Assert.AreEqual(1000, result.PerProject.Sum(p => p.MaximumPoints));
            Assert.AreEqual(exam.N - selected.Count, result.WrongTasks.Count);
            Assert.AreEqual(result.WrongTasks.Count, log.Count);
            foreach (var wrong in result.WrongTasks)
            {
                Assert.IsTrue(wrong.ProjectCode.StartsWith("P"));
                var project = exam.Projects.Single(p => p.ProjectId == "Word2019_" + wrong.ProjectCode);
                Assert.AreEqual(project.InstructionKeys[wrong.TaskId], wrong.InstructionKey);
            }
            Assert.IsFalse(Newtonsoft.Json.JsonConvert.SerializeObject(result).Contains("PRIVATE"));
        }

        [TestMethod]
        public void MissingAndFailResultsAreWrongButUnknownDuplicateAndMismatchedResultsAreRejected()
        {
            var packages = Packages();
            var exam = new ExamComposer(Catalog(packages), packages).Compose(42);
            var scorer = new ExamScorer(_ => Assert.Fail("Fail or missing results have no Error diagnostics."));
            var first = exam.Projects[0];
            var fail = new ExamTaskGrade(first.ProjectId, first.TaskIds[0], new TaskGradeResult(TaskGradeOutcome.Fail, "hidden", "test", first.TaskIds[0]));
            Assert.AreEqual(exam.N, scorer.Score(exam, new[] { fail }).WrongTasks.Count);
            Assert.ThrowsException<InvalidDataException>(() => scorer.Score(exam, new[] { fail, fail }));
            Assert.ThrowsException<InvalidDataException>(() => scorer.Score(exam, new[] {
                new ExamTaskGrade("Word2019_P99", "T01", fail.Result) }));
            Assert.ThrowsException<InvalidDataException>(() => scorer.Score(exam, new[] {
                new ExamTaskGrade(first.ProjectId, first.TaskIds[1], fail.Result) }));
        }

        [TestMethod]
        public void RandomAlgorithmHasFixedCrossLanguageGoldenVector()
        {
            var random = new ExamRandom(0);
            CollectionAssert.AreEqual(new[] { 0xE220A8397B1DCDAFUL, 0x6E789E6AA1B965F4UL, 0x06C45D188009454FUL },
                new[] { random.Next(), random.Next(), random.Next() });
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => random.Below(0));
            CollectionAssert.AreNotEqual(ScoreAllocator.Allocate(35, 0), ScoreAllocator.Allocate(35, 1));
        }

        [DataTestMethod]
        [DataRow("excludedRequired")]
        [DataRow("excludedGrouped")]
        [DataRow("unknownTask")]
        [DataRow("duplicateTask")]
        [DataRow("missingEntry")]
        [DataRow("unknownProject")]
        [DataRow("conflict")]
        [DataRow("legacy")]
        [DataRow("total")]
        [DataRow("mandatorySeven")]
        public void InvalidOrInfeasibleCatalogsFailBeforeComposition(string mutation)
        {
            var packages = SyntheticPackages(7, mutation == "total" ? 4 : 7);
            var entries = SyntheticCatalog(packages);
            if (mutation == "excludedRequired") { entries[0].RequiredTaskIds.Add("T01"); entries[0].ExcludedTaskIds.Add("T01"); }
            if (mutation == "excludedGrouped") { entries[0].TaskGroups.Add(new List<string> { "T01", "T02" }); entries[0].ExcludedTaskIds.Add("T02"); }
            if (mutation == "unknownTask") entries[0].RequiredTaskIds.Add("missing");
            if (mutation == "duplicateTask") entries[0].RequiredTaskIds.AddRange(new[] { "T01", "t01" });
            if (mutation == "missingEntry") entries.RemoveAt(0);
            if (mutation == "unknownProject") entries[0].ProjectId = "Word2019_P99";
            if (mutation == "conflict") entries[0].ConflictGroup = entries[1].ConflictGroup = "same";
            if (mutation == "legacy") packages[0].Meta.Starter = packages[1].Meta.Starter = "starter.doc";
            if (mutation == "mandatorySeven") entries[0].RequiredTaskIds = packages[0].Tasks.Select(t => t.TaskId).ToList();
            Assert.ThrowsException<InvalidDataException>(() => new ExamComposer(entries, packages));
        }

        [TestMethod]
        public void OverlappingGroupsRequiredClosureAndExcludedProjectsAreHonored()
        {
            var packages = SyntheticPackages(8, 8);
            var entries = SyntheticCatalog(packages);
            entries[7].Eligible = false;
            entries[0].RequiredTaskIds.Add("T01");
            entries[0].TaskGroups.Add(new List<string> { "T01", "T02", "T03" });
            entries[0].TaskGroups.Add(new List<string> { "T03", "T04", "T05" });
            var composer = new ExamComposer(entries, packages);
            for (int seed = 0; seed < 100; seed++)
            {
                var exam = composer.Compose(seed);
                Assert.IsFalse(exam.Projects.Any(p => p.ProjectId == packages[7].Meta.ProjectId));
                var project = exam.Projects.Single(p => p.ProjectId == packages[0].Meta.ProjectId);
                Assert.IsTrue(project.TaskIds.Count >= 5);
                foreach (string id in new[] { "T01", "T02", "T03", "T04", "T05" }) CollectionAssert.Contains(project.TaskIds.ToList(), id);
            }
        }

        [TestMethod]
        public void PreparedComposerAndDefinitionsDoNotRetainMutableInputs()
        {
            var packages = SyntheticPackages(7, 6);
            var catalog = SyntheticCatalog(packages);
            var composer = new ExamComposer(catalog, packages);
            string before = Fingerprint(composer.Compose(42));
            catalog[0].ExcludedTaskIds.Add("T01");
            catalog.Clear();
            packages[0].Tasks[0].InstructionKey = "changed";
            packages.Clear();
            var definition = composer.Compose(42);
            Assert.AreEqual(before, Fingerprint(definition));
            Assert.ThrowsException<NotSupportedException>(() => ((IList<ExamProjectDefinition>)definition.Projects).Clear());
            Assert.ThrowsException<NotSupportedException>(() => ((IList<string>)definition.Projects[0].TaskIds).Clear());
            Assert.ThrowsException<NotSupportedException>(() => ((IDictionary<string, string>)definition.Projects[0].InstructionKeys).Clear());
        }

        [TestMethod]
        public void DefinitionAndScoreRejectMalformedInputWithoutSilentlyAwardingPoints()
        {
            var packages = SyntheticPackages(7, 5);
            var projects = packages.Select(p => new ExamProjectDefinition(p.Meta.ProjectId, p.Tasks)).ToList();
            Assert.ThrowsException<InvalidDataException>(() => new ExamDefinition(0, projects.Take(6)));
            Assert.ThrowsException<InvalidDataException>(() => new ExamDefinition(0, projects.Take(6).Concat(new[] { projects[0] })));
            Assert.ThrowsException<InvalidDataException>(() => new ExamProjectDefinition("wrong", packages[0].Tasks));
            Assert.ThrowsException<InvalidDataException>(() => new ExamProjectDefinition("Word2019_P01\n", packages[0].Tasks));
            Assert.ThrowsException<InvalidDataException>(() => new ExamProjectDefinition(packages[0].Meta.ProjectId, packages[0].Tasks.Take(3)));
            Assert.ThrowsException<ArgumentNullException>(() => new ExamScorer(null));
            var scorer = new ExamScorer(_ => { });
            var exam = new ExamDefinition(0, projects);
            Assert.ThrowsException<ArgumentNullException>(() => scorer.Score(null, new ExamTaskGrade[0]));
            Assert.ThrowsException<ArgumentNullException>(() => scorer.Score(exam, null));
            Assert.ThrowsException<InvalidDataException>(() => scorer.Score(exam, new ExamTaskGrade[] { null }));
            Assert.ThrowsException<InvalidDataException>(() => scorer.Score(exam, new[] { new ExamTaskGrade(projects[0].ProjectId,
                "T01", new TaskGradeResult((TaskGradeOutcome)999, "private", "test", "T01")) }));
            Assert.AreEqual(0, scorer.Score(exam, new ExamTaskGrade[0]).Total);
        }

        [TestMethod]
        public void GoldenExamAndAllocationRemainStableForTheCurrentCatalog()
        {
            var packages = Packages();
            Assert.AreEqual("42:Word2019_P23:T01,T02,T03,T04,T05,T06|Word2019_P02:T01,T03,T07,T08|Word2019_P17:T01,T02,T03,T04,T05,T06|Word2019_P25:T01,T03,T04,T05|Word2019_P16:T02,T03,T04,T05,T06,T07|Word2019_P04:T01,T02,T03,T04,T06,T08|Word2019_P13:T01,T02,T04,T05,T06", Fingerprint(ExamComposer.Compose(Catalog(packages), packages, 42)));
            Assert.AreEqual("28,28,29,28,29,29,29,28,29,28,29,29,29,29,29,28,28,28,28,29,29,29,29,29,28,28,28,29,29,29,29,28,28,29,28", string.Join(",", ScoreAllocator.Allocate(35, 42)));
        }

        [TestMethod]
        public void RestrictiveFeasiblePoolTerminatesAndRespectsConflicts()
        {
            var packages = SyntheticPackages(28, 5);
            var catalog = SyntheticCatalog(packages);
            // Force five tasks/project so this fixture has exactly N=35.
            foreach (var entry in catalog) entry.RequiredTaskIds.AddRange(packages[0].Tasks.Select(t => t.TaskId));
            foreach (var entry in catalog.Skip(7)) entry.ConflictGroup = "one-shared-slot";
            var composer = new ExamComposer(catalog, packages);
            foreach (long seed in new[] { 0L, 1L, -1L })
            {
                var exam = composer.Compose(seed);
                Assert.AreEqual(35, exam.N);
                Assert.IsTrue(exam.Projects.Count(p => int.Parse(p.ProjectId.Substring("Word2019_P".Length)) > 7) <= 1);
                Assert.AreEqual(Fingerprint(exam), Fingerprint(composer.Compose(seed)));
            }
        }

        [TestMethod]
        public void ScoreUsesTaskIdentityRatherThanResultOrder()
        {
            var packages = Packages();
            var exam = new ExamComposer(Catalog(packages), packages).Compose(42);
            var grades = exam.Projects.SelectMany(p => p.TaskIds.Select(id => new ExamTaskGrade(p.ProjectId, id,
                new TaskGradeResult(p.ProjectId == exam.Projects[0].ProjectId ? TaskGradeOutcome.Pass : TaskGradeOutcome.Fail,
                    "private", "test", id)))).ToArray();
            var scorer = new ExamScorer(_ => Assert.Fail("No Error expected."));
            var result = scorer.Score(exam, grades.Reverse());
            Assert.AreEqual(result.PerProject[0].MaximumPoints, result.Total);
            Assert.AreEqual(result.Total, result.PerProject[0].Points);
            Assert.IsTrue(result.PerProject.Skip(1).All(p => p.Points == 0));
            CollectionAssert.AreEqual(exam.Projects.Skip(1).SelectMany(p => p.TaskIds.Select(id => p.ProjectId + "/" + id)).ToArray(),
                result.WrongTasks.Select(w => "Word2019_" + w.ProjectCode + "/" + w.TaskId).ToArray());
        }

        private static List<ProjectPackage> SyntheticPackages(int count, int tasks)
        {
            return Enumerable.Range(1, count).Select(i => new ProjectPackage
            {
                Meta = new ProjectMeta { ProjectId = "Word2019_P" + i.ToString("00") },
                Tasks = Enumerable.Range(1, tasks).Select(t => new TaskDefinition
                { TaskId = "T" + t.ToString("00"), InstructionKey = "P" + i.ToString("00") + "_T" + t.ToString("00") + "_INST" }).ToList()
            }).ToList();
        }
        private static List<TestingCatalogEntry> SyntheticCatalog(IEnumerable<ProjectPackage> packages) =>
            packages.Select(p => new TestingCatalogEntry { ProjectId = p.Meta.ProjectId, Eligible = true }).ToList();

        private static List<ProjectPackage> Packages() => new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en").ToList();
        private static IReadOnlyList<TestingCatalogEntry> Catalog(IEnumerable<ProjectPackage> packages) =>
            new TestingCatalogLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages);
        private static string Fingerprint(ExamDefinition exam) => exam.Seed + ":" + string.Join("|", exam.Projects.Select(p => p.ProjectId + ":" + string.Join(",", p.TaskIds)));
        private static HashSet<int> CorrectSubset(int[] points, int target)
        {
            var ways = new Dictionary<int, HashSet<int>> { { 0, new HashSet<int>() } };
            for (int i = 0; i < points.Length; i++)
                foreach (var sum in ways.Keys.OrderByDescending(n => n).ToArray())
                    if (sum + points[i] <= target && !ways.ContainsKey(sum + points[i]))
                        ways.Add(sum + points[i], new HashSet<int>(ways[sum]) { i });
            Assert.IsTrue(ways.ContainsKey(target), "This fixture must realize the exact threshold.");
            return ways[target];
        }
    }
}
