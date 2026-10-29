using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class GradingErrorTests
    {
        public sealed class ErrorFixture
        {
            public string Id { get; set; }
            public string ProjectId { get; set; }
            public string TaskId { get; set; }
            public string AssertionType { get; set; }
            public string Fixture { get; set; }
            public string SourceFixture { get; set; }
            public string Expected { get; set; }
            public string Helper { get; set; }
            public string MetadataKey { get; set; }
            public JToken BadValue { get; set; }
            public string InjectedException { get; set; }
        }

        private static List<ErrorFixture> LoadFixtures()
        {
            var fixtures = JsonConvert.DeserializeObject<List<ErrorFixture>>(File.ReadAllText(
                Path.Combine(TestCaseData.FixturesRoot, "a2-errors.json")));
            Assert.IsNotNull(fixtures);
            return fixtures;
        }

        private static TaskDefinition OriginalTask(ErrorFixture data)
        {
            var package = new ProjectLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, data.ProjectId), "en");
            Assert.IsNotNull(package, "Load the unchanged production package through ProjectLoader.");
            var task = package.Tasks.Single(t => t.TaskId == data.TaskId);
            Assert.AreEqual(data.AssertionType, task.AssertionType);
            return task;
        }

        private static TaskDefinition CloneTask(TaskDefinition original)
        {
            return new TaskDefinition
            {
                ProjectId = original.ProjectId, TaskId = original.TaskId,
                AssertionType = original.AssertionType, TitleKey = original.TitleKey,
                InstructionKey = original.InstructionKey,
                Extra = original.Extra.ToDictionary(p => p.Key, p => p.Value.DeepClone(), StringComparer.OrdinalIgnoreCase)
            };
        }

        public static IEnumerable<object[]> MetadataCases() => LoadFixtures()
            .Where(f => f.MetadataKey != null).Select(f => new object[] { f.Id });

        public static string CaseName(MethodInfo method, object[] data) => (string)data[0];

        [DataTestMethod]
        [DynamicData(nameof(MetadataCases), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(CaseName))]
        public void MalformedMetadataReturnsErrorAfterValidControl(string id)
        {
            var data = LoadFixtures().Single(f => f.Id == id);
            var original = OriginalTask(data);
            string before = JsonConvert.SerializeObject(original);
            var changed = CloneTask(original);
            Assert.IsTrue(changed.Extra.ContainsKey(data.MetadataKey), "Override an existing metadata key only.");
            changed.Extra[data.MetadataKey] = data.BadValue.DeepClone();
            string fixture = TestCaseData.FixturePath(data.Fixture);
            CollectionAssert.AreEqual(File.ReadAllBytes(TestCaseData.FixturePath(data.SourceFixture)), File.ReadAllBytes(fixture),
                "Error labels describe task metadata, not modified OOXML.");
            using (var workspace = new TestWorkspace())
            {
                var grader = new WordGradingService(() => workspace.Folder);
                var outputLedger = new OutputOwnershipLedger(new ProjectPackage { Tasks = new List<TaskDefinition> { original } },
                    workspace.Folder, workspace.Folder, _ => { });
                if (data.AssertionType == "FileExistsInDocuments")
                    workspace.Copy(fixture, (string)original.Extra["expectedFileName"]);
                var context = new TrainingGradeContext { OutputOwnership = outputLedger.Capture() };
                Assert.AreEqual(TaskGradeOutcome.Pass, grader.CheckTask(fixture, original, context).Outcome,
                    "The unchanged task must reach and pass its original assertion on the same saved fixture.");
                var result = grader.CheckTask(fixture, changed, context);
                Assert.AreEqual(TaskGradeOutcome.Error, result.Outcome, id + ": " + result.Message);
                StringAssert.Contains(result.Message, data.MetadataKey, "Identify the malformed metadata key.");
                Assert.AreEqual(original.TaskId, result.TaskId);
                Assert.AreEqual(original.AssertionType, result.AssertionType);
                Assert.AreEqual(before, JsonConvert.SerializeObject(original), "Do not mutate the production task.");
            }
        }

        [DataTestMethod]
        [DataRow("IOException")]
        [DataRow("InvalidDataException")]
        [DataRow("UnauthorizedAccessException")]
        [DataRow("XmlException")]
        [DataRow("ArgumentException")]
        [DataRow("InvalidOperationException")]
        [DataRow("NullReferenceException")]
        [DataRow("InvalidCastException")]
        [DataRow("FormatException")]
        [DataRow("KeyNotFoundException")]
        [DataRow("Exception")]
        public void RecoverableExceptionReturnsErrorWithDiagnostic(string exceptionType)
        {
            var data = LoadFixtures().Single(f => f.InjectedException != null);
            var task = OriginalTask(data);
            string before = JsonConvert.SerializeObject(task);
            var exception = InjectedException(exceptionType);
            int calls = 0;
            var grader = new WordGradingService(() => { calls++; throw exception; });
            var result = grader.CheckTask(TestCaseData.FixturePath(data.Fixture), task);
            Assert.AreEqual(1, calls, "Exercise the existing Documents-root callback inside public CheckTask.");
            Assert.AreEqual(TaskGradeOutcome.Error, result.Outcome, "Technical faults must never become learner Fail.");
            StringAssert.Contains(result.Message, exceptionType);
            StringAssert.Contains(result.Message, exception.Message);
            Assert.AreEqual(task.TaskId, result.TaskId);
            Assert.AreEqual(task.AssertionType, result.AssertionType);
            Assert.AreEqual(before, JsonConvert.SerializeObject(task));
        }

        [DataTestMethod]
        [DataRow("OutOfMemoryException")]
        [DataRow("StackOverflowException")]
        public void FatalExceptionEscapesUnchanged(string exceptionType)
        {
            // Manually throw exception objects; never exhaust memory or recurse.
            Exception injected = exceptionType == "OutOfMemoryException"
                ? (Exception)new OutOfMemoryException("A2 manually injected fatal exception")
                : new StackOverflowException("A2 manually injected fatal exception");
            var data = LoadFixtures().Single(f => f.InjectedException != null);
            int calls = 0;
            var grader = new WordGradingService(() => { calls++; throw injected; });
            Exception escaped = null;
            try { grader.CheckTask(TestCaseData.FixturePath(data.Fixture), OriginalTask(data)); }
            catch (Exception ex) { escaped = ex; }
            Assert.AreEqual(1, calls);
            Assert.AreSame(injected, escaped, "Fatal exceptions must escape instead of being reported as grading results.");
        }

        [TestMethod]
        public void ErrorManifestHasEachRequiredHelperAndANewCatchPath()
        {
            var fixtures = LoadFixtures();
            Assert.AreEqual(fixtures.Count, fixtures.Select(f => f.Id).Distinct().Count());
            CollectionAssert.AreEquivalent(new[] { "RequiredString", "RequiredInt", "RequiredArray", "RequiredStringMatrix", "RequiredBool" },
                fixtures.Where(f => f.MetadataKey != null).Select(f => f.Helper).ToArray());
            Assert.IsTrue(fixtures.Any(f => f.InjectedException == "InvalidOperationException"));
            foreach (var fixture in fixtures)
            {
                Assert.AreEqual("Error", fixture.Expected);
                Assert.IsTrue(fixture.Fixture.StartsWith(fixture.ProjectId + "/" + fixture.TaskId + "/error", StringComparison.Ordinal));
                string path = TestCaseData.FixturePath(fixture.Fixture);
                Assert.AreEqual(".docx", Path.GetExtension(path));
                Assert.IsTrue(new FileInfo(path).Length <= 2000000);
                CollectionAssert.AreEqual(File.ReadAllBytes(TestCaseData.FixturePath(fixture.SourceFixture)), File.ReadAllBytes(path));
                OriginalTask(fixture);
            }
        }

        private static Exception InjectedException(string type)
        {
            const string message = "A2 injected recoverable grading fault";
            switch (type)
            {
                case "IOException": return new IOException(message);
                case "InvalidDataException": return new InvalidDataException(message);
                case "UnauthorizedAccessException": return new UnauthorizedAccessException(message);
                case "XmlException": return new XmlException(message);
                case "ArgumentException": return new ArgumentException(message);
                case "InvalidOperationException": return new InvalidOperationException(message);
                case "NullReferenceException": return new NullReferenceException(message);
                case "InvalidCastException": return new InvalidCastException(message);
                case "FormatException": return new FormatException(message);
                case "KeyNotFoundException": return new KeyNotFoundException(message);
                case "Exception": return new Exception(message);
                default: throw new ArgumentException("Unknown test exception: " + type);
            }
        }
    }
}
