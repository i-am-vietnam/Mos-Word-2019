using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using Newtonsoft.Json;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class NearMissFixtureTests
    {
        private static readonly string[] AssertionTypes =
        {
            "BodyTextReplaceAll", "TextReplaceAll", "TextRangeFormattingEquals", "Footnote", "SymbolInserted"
        };

        public sealed class NearMissCase
        {
            public string Id { get; set; }
            public string ProjectId { get; set; }
            public string TaskId { get; set; }
            public string AssertionType { get; set; }
            public string PositiveFixture { get; set; }
            public string NearMissFixture { get; set; }
            public string Defect { get; set; }
        }

        private static List<NearMissCase> LoadCases()
        {
            var cases = JsonConvert.DeserializeObject<List<NearMissCase>>(File.ReadAllText(
                Path.Combine(TestCaseData.FixturesRoot, "a2-nearmiss.json")));
            Assert.IsNotNull(cases);
            Assert.AreEqual(TestInventory.ExpectedNearMissTaskCount, cases.Count,
                "When adding a task in these five assertion families, update TestInventory.ExpectedNearMissTaskCount in TestCaseData.cs and add its correct/near-miss pair.");
            return cases;
        }

        public static IEnumerable<object[]> Cases()
        {
            return LoadCases().SelectMany(c => new[]
            {
                new object[] { c.Id, true }, new object[] { c.Id, false }
            });
        }

        public static string CaseName(MethodInfo method, object[] data)
        {
            return (string)data[0] + ((bool)data[1] ? "_Correct_Pass" : "_NearMiss_Fail");
        }

        [DataTestMethod]
        [DynamicData(nameof(Cases), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(CaseName))]
        public void OriginalTaskGradesSavedCorrectAndNearMiss(string id, bool correct)
        {
            NearMissCase data = LoadCases().Single(c => c.Id == id);
            var package = new ProjectLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, data.ProjectId), "en");
            Assert.IsNotNull(package, "The original production package must load.");
            TaskDefinition task = package.Tasks.Single(t => t.TaskId == data.TaskId);
            Assert.AreEqual(data.AssertionType, task.AssertionType);
            Assert.IsFalse(string.IsNullOrWhiteSpace(data.Defect), "Each near-miss needs an explicit task defect.");
            string fixture = TestCaseData.FixturePath(correct ? data.PositiveFixture : data.NearMissFixture);
            TaskGradeResult result = new WordGradingService().CheckTask(fixture, task);
            Assert.AreEqual(correct ? TaskGradeOutcome.Pass : TaskGradeOutcome.Fail, result.Outcome,
                id + ": " + data.Defect + " Grader: " + result.Message);
        }

        [TestMethod]
        public void EveryTaskInTheFiveFamiliesHasItsOwnSavedNearMiss()
        {
            var cases = LoadCases();
            var expected = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en")
                .SelectMany(p => p.Tasks.Where(t => AssertionTypes.Contains(t.AssertionType))
                    .Select(t => p.Meta.ProjectId + "/" + t.TaskId)).ToList();
            var actual = cases.Select(c => c.ProjectId + "/" + c.TaskId).ToList();
            CollectionAssert.AreEquivalent(expected, actual);
            Assert.AreEqual(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(cases.Count, cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(cases.Count, cases.Select(c => c.NearMissFixture).Distinct(StringComparer.Ordinal).Count());
            foreach (NearMissCase data in cases)
            {
                string folder = data.ProjectId + "/" + data.TaskId + "/";
                Assert.IsTrue(data.PositiveFixture.StartsWith(folder, StringComparison.Ordinal));
                Assert.IsTrue(data.NearMissFixture.StartsWith(folder + "nearmiss", StringComparison.Ordinal));
                foreach (string relative in new[] { data.PositiveFixture, data.NearMissFixture })
                {
                    string path = TestCaseData.FixturePath(relative);
                    Assert.AreEqual(".docx", Path.GetExtension(path));
                    Assert.IsTrue(new FileInfo(path).Length < 2000000, "Fixture must be below 2 MB: " + relative);
                }
            }
        }
    }
}
