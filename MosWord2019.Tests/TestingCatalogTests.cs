using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TestingCatalogTests
    {
        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void CurrentCatalogValidatesAtLoad(string language)
        {
            var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
            var entries = new TestingCatalogLoader().Load(CatalogPath, packages);
            Assert.AreEqual(TestInventory.ExpectedPackageCount, entries.Count,
                "Update TestInventory.ExpectedPackageCount and review testing-catalog.json when adding a package.");
            Assert.AreEqual(TestInventory.ExpectedTaskCount, packages.Sum(p => p.Tasks.Count));
            Assert.AreEqual(28, entries.Count(e => e.Eligible));
        }

        public static IEnumerable<object[]> Projects()
        {
            return Enumerable.Range(1, 28).Select(i => new object[] { "Word2019_P" + i.ToString("00") });
        }

        [DataTestMethod]
        [DynamicData(nameof(Projects), DynamicDataSourceType.Method)]
        public void EveryEligibleProjectHasFeasibleSubsets(string projectId)
        {
            var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en");
            var entry = new TestingCatalogLoader().Load(CatalogPath, packages).Single(e => e.ProjectId == projectId);
            Assert.IsTrue(entry.Eligible);
            var counts = new TestingCatalogValidator().GetSelectableTaskCounts(entry, packages.Single(p => p.Meta.ProjectId == projectId));
            Assert.IsTrue(counts.Count > 0 && counts.All(n => n >= 4 && n <= 6), projectId);
        }

        [TestMethod]
        public void CatalogCopiesHaveIdenticalBytesAndOnlyOneNewRootFile()
        {
            string source = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "MosWord2019.WinForms", "Projects"));
            foreach (string root in new[] { source, TestCaseData.ProjectsRoot })
                Assert.AreEqual(TestInventory.ExpectedPackageFileCount + 1,
                    Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length,
                    "Only testing-catalog.json is additional to the original package-file inventory.");
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(source, "testing-catalog.json")), File.ReadAllBytes(CatalogPath));
        }

        [TestMethod]
        public void CatalogPreservesReviewedDependenciesAndSnapshotExclusions()
        {
            var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en");
            var entries = new TestingCatalogLoader().Load(CatalogPath, packages).ToDictionary(e => e.ProjectId);
            CollectionAssert.AreEqual(new[] { "T01" }, entries["Word2019_P07"].RequiredTaskIds);
            CollectionAssert.AreEqual(new[] { "T03", "T04" }, entries["Word2019_P14"].TaskGroups.Single());
            CollectionAssert.AreEqual(new[] { "T05", "T07" }, entries["Word2019_P18"].TaskGroups.Single());
            CollectionAssert.AreEqual(new[] { "T07" }, entries["Word2019_P22"].ExcludedTaskIds);
            CollectionAssert.AreEqual(new[] { "T03" }, entries["Word2019_P24"].ExcludedTaskIds);
            foreach (var pair in new[] { new[] { "04", "T05" }, new[] { "13", "T03" }, new[] { "16", "T01" } })
            {
                var package = packages.Single(p => p.Meta.ProjectId == "Word2019_P" + pair[0]);
                Assert.AreEqual(TaskGradeOutcome.Pass, new WordGradingService().CheckTask(
                    Path.Combine(package.ProjectFolderPath, package.Meta.Starter), package.Tasks.Single(t => t.TaskId == pair[1])).Outcome);
                CollectionAssert.Contains(entries[package.Meta.ProjectId].ExcludedTaskIds, pair[1]);
            }
            Assert.AreEqual(5, entries.Values.Sum(e => e.ExcludedTaskIds.Count));
        }

        [TestMethod]
        public void CrossProjectDeclaredFilenameCollisionsHaveConflictGroups()
        {
            var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en");
            var entries = new TestingCatalogLoader().Load(CatalogPath, packages).ToDictionary(e => e.ProjectId);
            var paths = new List<Tuple<string, string, string>>();
            foreach (var package in packages)
            {
                string id = package.Meta.ProjectId;
                string assets = Path.Combine(package.ProjectFolderPath, "assets");
                if (Directory.Exists(assets))
                    foreach (string file in Directory.GetFiles(assets, "*", SearchOption.AllDirectories))
                        if (package.Meta.AssetStaging == null || package.Meta.AssetStaging.CopyAssetsToDocuments)
                            paths.Add(Tuple.Create("Documents/" + Path.GetFileName(file), id, Hash(file)));
                if (package.Meta.AssetStaging != null)
                    foreach (var copy in package.Meta.AssetStaging.AdditionalCopies)
                        paths.Add(Tuple.Create(copy.Destination + "/" + (copy.DestinationFileName ?? Path.GetFileName(copy.Source)),
                            id, Hash(Path.Combine(assets, copy.Source))));
                foreach (var task in package.Tasks)
                {
                    bool template = task.AssertionType == "FileExistsInCustomOfficeTemplates" || task.AssertionType == "SavedWordTemplate";
                    bool documents = task.AssertionType == "FileExistsInDocuments" || task.AssertionType == "PlainTextDocumentExport";
                    if (template || documents)
                        paths.Add(Tuple.Create((template ? "Templates/" : "Documents/") + (string)task.Extra["expectedFileName"], id, "output:" + id));
                }
            }
            var conflicts = paths.GroupBy(p => p.Item1, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(p => p.Item2).Distinct().Count() > 1 && g.Select(p => p.Item3).Distinct().Count() > 1).ToList();
            Assert.AreEqual(1, conflicts.Count, "Review all filename collisions and update the Testing catalog/review if packages change.");
            foreach (var conflict in conflicts)
            {
                var groups = conflict.Select(p => entries[p.Item2].ConflictGroup).Distinct().ToArray();
                Assert.AreEqual(1, groups.Length, conflict.Key);
                Assert.IsFalse(string.IsNullOrWhiteSpace(groups[0]), conflict.Key);
            }
        }

        private static string Hash(string path)
        {
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path)) return Convert.ToBase64String(hash.ComputeHash(stream));
        }
        private static string CatalogPath => Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json");
    }
}
