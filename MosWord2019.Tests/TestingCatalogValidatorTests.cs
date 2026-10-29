using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TestingCatalogValidatorTests
    {
        [TestMethod]
        public void SevenFiveTaskProjectsFormValidPool()
        {
            var packages = Packages(7, 5);
            Assert.IsFalse(new TestingCatalogValidator().Validate(Entries(packages), packages).HasErrors);
        }

        [TestMethod]
        public void OverlappingGroupsHaveTransitiveAllOrNoneClosure()
        {
            var package = Packages(1, 8)[0];
            var entry = Entries(new[] { package })[0];
            entry.TaskGroups.Add(new List<string> { "T1", "T2", "T3" });
            entry.TaskGroups.Add(new List<string> { "T3", "T4", "T5" });
            entry.RequiredTaskIds.Add("T1");
            CollectionAssert.AreEqual(new[] { 5, 6 }, new TestingCatalogValidator().GetSelectableTaskCounts(entry, package).ToArray());
            entry.RequiredTaskIds.AddRange(new[] { "T6", "T7" });
            Assert.AreEqual(0, new TestingCatalogValidator().GetSelectableTaskCounts(entry, package).Count);
        }

        [DataTestMethod]
        [DataRow("unknown", "CATALOG_TASK_UNKNOWN")]
        [DataRow("duplicate", "CATALOG_TASK_DUPLICATE")]
        [DataRow("null", "CATALOG_TASK_INVALID")]
        [DataRow("overlap", "CATALOG_REQUIRED_EXCLUDED")]
        [DataRow("groupExcluded", "CATALOG_GROUP_EXCLUDED")]
        [DataRow("group", "CATALOG_GROUP_INVALID")]
        [DataRow("impossible", "CATALOG_PROJECT_INFEASIBLE")]
        public void InvalidTaskMetadataReturnsIssues(string mutation, string code)
        {
            var packages = Packages(7, 7);
            var entries = Entries(packages);
            var entry = entries[0];
            if (mutation == "unknown") entry.RequiredTaskIds.Add("missing");
            if (mutation == "duplicate") entry.RequiredTaskIds.AddRange(new[] { "T1", "t1" });
            if (mutation == "null") entry.RequiredTaskIds.Add(null);
            if (mutation == "overlap") { entry.RequiredTaskIds.Add("T1"); entry.ExcludedTaskIds.Add("T1"); }
            if (mutation == "groupExcluded") { entry.TaskGroups.Add(new List<string> { "T1", "T2" }); entry.ExcludedTaskIds.Add("T2"); }
            if (mutation == "group") entry.TaskGroups.Add(new List<string> { "T1" });
            if (mutation == "impossible") entry.TaskGroups.Add(packages[0].Tasks.Select(t => t.TaskId).ToList());
            HasCode(entries, packages, code);
        }

        [TestMethod]
        public void ProjectIdentityMustCoverPackagesExactlyOnce()
        {
            var packages = Packages(7, 6);
            var entries = Entries(packages);
            entries[0].ProjectId = "unknown";
            entries.Add(entries[1]);
            HasCode(entries, packages, "CATALOG_PROJECT_UNKNOWN");
            HasCode(entries, packages, "CATALOG_PROJECT_MISSING");
            HasCode(entries, packages, "CATALOG_PROJECT_DUPLICATE");
        }

        [DataTestMethod]
        [DataRow("conflict")]
        [DataRow("legacy")]
        [DataRow("total")]
        [DataRow("few")]
        public void PoolNearMissesCannotFormExam(string mutation)
        {
            var packages = Packages(7, mutation == "total" ? 4 : mutation == "few" ? 3 : 5);
            var entries = Entries(packages);
            if (mutation == "conflict") entries[0].ConflictGroup = entries[1].ConflictGroup = "same";
            if (mutation == "legacy") packages[0].Meta.Starter = packages[1].Meta.Starter = "starter.doc";
            HasCode(entries, packages, "CATALOG_POOL_INFEASIBLE");
            if (mutation == "few") HasCode(entries, packages, "CATALOG_SELECTABLE_TOO_FEW");
        }

        [TestMethod]
        public void PoolCanSkipIneligibleAndConflictingAlternatives()
        {
            var packages = Packages(9, 5);
            var entries = Entries(packages);
            entries[0].Eligible = false;
            entries[1].ConflictGroup = entries[2].ConflictGroup = "same";
            packages[1].Meta.Starter = packages[2].Meta.Starter = "starter.doc";
            Assert.IsFalse(new TestingCatalogValidator().Validate(entries, packages).HasErrors);
        }

        [TestMethod]
        public void PoolUsesActualFeasibleTaskCountsAndFortyTaskBoundary()
        {
            var packages = Packages(7, 6);
            var entries = Entries(packages);
            foreach (var entry in entries.Take(6))
                entry.RequiredTaskIds.AddRange(new[] { "T1", "T2", "T3", "T4", "T5", "T6" });
            entries[6].RequiredTaskIds.AddRange(new[] { "T1", "T2", "T3", "T4" });
            Assert.IsFalse(new TestingCatalogValidator().Validate(entries, packages).HasErrors);
            entries[6].RequiredTaskIds.Add("T5");
            HasCode(entries, packages, "CATALOG_POOL_INFEASIBLE");
        }

        [TestMethod]
        public void NullMetadataCollectionsAndEntriesBecomeValidationIssues()
        {
            var packages = Packages(7, 5);
            var entries = Entries(packages);
            entries[0].RequiredTaskIds = null;
            entries[1].ExcludedTaskIds = null;
            entries[2].TaskGroups = null;
            entries[3].TaskGroups.Add(null);
            entries.Add(null);
            HasCode(entries, packages, "CATALOG_TASK_INVALID");
            HasCode(entries, packages, "CATALOG_GROUP_INVALID");
            HasCode(entries, packages, "CATALOG_PROJECT_INVALID");
            Assert.IsTrue(new TestingCatalogValidator().Validate(null, null).HasErrors);
        }

        [TestMethod]
        public void LoaderRejectsMissingKeysDuplicateKeysAndMalformedJson()
        {
            var packages = Packages(7, 5);
            var json = JArray.Parse(JsonConvert.SerializeObject(Entries(packages)));
            ((JObject)json[0]).Remove("notes");
            WithCatalog(json.ToString(), path => StringAssert.Contains(
                Assert.ThrowsException<InvalidDataException>(() => new TestingCatalogLoader().Load(path, packages)).Message,
                "CATALOG_SCHEMA_INVALID"));
            foreach (string malformed in new[] { "[", "[{\"eligible\":true,\"eligible\":false}]" })
                WithCatalog(malformed, path => StringAssert.Contains(
                    Assert.ThrowsException<InvalidDataException>(() => new TestingCatalogLoader().Load(path, packages)).Message,
                    "CATALOG_JSON_INVALID"));
        }

        [DataTestMethod]
        [DataRow("eligible", "\"true\"")]
        [DataRow("requiredTaskIds", "null")]
        [DataRow("taskGroups", "[[1,2]]")]
        [DataRow("notes", "123")]
        [DataRow("conflictGroup", "false")]
        [DataRow("projectId", "null")]
        [DataRow("excludedTaskIds", "[null]")]
        [DataRow("typo", "[]")]
        public void LoaderRejectsMalformedSchema(string property, string value)
        {
            var packages = Packages(7, 5);
            var json = JArray.Parse(JsonConvert.SerializeObject(Entries(packages)));
            ((JObject)json[0])[property] = JToken.Parse(value);
            WithCatalog(json.ToString(), path => StringAssert.Contains(
                Assert.ThrowsException<InvalidDataException>(() => new TestingCatalogLoader().Load(path, packages)).Message,
                "CATALOG_SCHEMA_INVALID"));
        }

        [TestMethod]
        public void LoaderValidatesBeforeReturningAndReportsMissingFile()
        {
            var packages = Packages(7, 5);
            WithCatalog(JsonConvert.SerializeObject(Entries(packages)), path =>
                Assert.AreEqual(7, new TestingCatalogLoader().Load(path, packages).Count));
            WithCatalog("[]", path => StringAssert.Contains(
                Assert.ThrowsException<InvalidDataException>(() => new TestingCatalogLoader().Load(path, packages)).Message,
                "CATALOG_PROJECT_MISSING"));
            StringAssert.Contains(Assert.ThrowsException<InvalidDataException>(() =>
                new TestingCatalogLoader().Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Guid.NewGuid() + ".json"), packages)).Message,
                "CATALOG_MISSING");
        }

        [TestMethod]
        public void LoaderPreservesDateLookingStringsAndRejectsTrailingValues()
        {
            var packages = Packages(7, 5);
            var entries = Entries(packages);
            entries[0].Notes = "2026-10-30T00:00:00Z";
            string json = JsonConvert.SerializeObject(entries);
            WithCatalog(json, path => Assert.AreEqual(entries[0].Notes,
                new TestingCatalogLoader().Load(path, packages)[0].Notes));
            WithCatalog(json + " []", path => StringAssert.Contains(
                Assert.ThrowsException<InvalidDataException>(() => new TestingCatalogLoader().Load(path, packages)).Message,
                "CATALOG_JSON_INVALID"));
        }

        private static void HasCode(IEnumerable<TestingCatalogEntry> entries, IEnumerable<ProjectPackage> packages, string code)
        {
            Assert.IsTrue(new TestingCatalogValidator().Validate(entries, packages).Issues.Any(i => i.Code == code), code);
        }

        private static List<ProjectPackage> Packages(int count, int tasks)
        {
            return Enumerable.Range(1, count).Select(i => new ProjectPackage
            {
                Meta = new ProjectMeta { ProjectId = "Word2019_P" + i.ToString("00"), Starter = "starter.docx" },
                Tasks = Enumerable.Range(1, tasks).Select(t => new TaskDefinition { TaskId = "T" + t }).ToList()
            }).ToList();
        }

        private static List<TestingCatalogEntry> Entries(IEnumerable<ProjectPackage> packages)
        {
            return packages.Select(p => new TestingCatalogEntry { ProjectId = p.Meta.ProjectId, Eligible = true }).ToList();
        }

        private static void WithCatalog(string json, Action<string> action)
        {
            using (var workspace = new TestWorkspace())
            {
                string path = Path.Combine(workspace.Folder, "catalog.json");
                File.WriteAllText(path, json);
                action(path);
            }
        }
    }
}
