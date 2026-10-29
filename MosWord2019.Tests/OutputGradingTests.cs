using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class OutputGradingTests
    {
        public static IEnumerable<object[]> ExistenceCases()
        {
            foreach (string project in new[] { "Word2019_P09", "Word2019_P06", "Word2019_P07" })
                foreach (string scenario in new[] { "new", "unchanged", "mtime-only", "hash-changed", "replacement-changed", "replacement-same-hash", "missing", "wrong-name" })
                    yield return new object[] { project, scenario };
        }

        [DataTestMethod]
        [DynamicData(nameof(ExistenceCases), DynamicDataSourceType.Method)]
        public void ExactDeclaredOutputRequiresFreshObservation(string projectId, string scenario)
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = LoadTask(projectId, "T08");
                string templates = TemplateRoot(workspace);
                string path = ExpectedPath(workspace, templates, task);
                if (scenario != "new" && scenario != "missing" && scenario != "wrong-name")
                    File.WriteAllText(path, "previous answer");
                DateTime started = DateTime.UtcNow;
                OutputFileState baseline = ReadState(path, started, "original-file");
                if (scenario == "new" || scenario == "hash-changed" || scenario == "replacement-changed")
                    File.WriteAllText(path, "current answer has changed bytes");
                if (scenario == "mtime-only") File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
                if (scenario == "replacement-same-hash")
                {
                    File.Delete(path);
                    File.WriteAllText(path, "previous answer");
                }
                if (scenario == "wrong-name") File.WriteAllText(path + ".wrong", "current answer");
                OutputFileState current = ReadState(path, DateTime.UtcNow,
                    scenario.StartsWith("replacement-", StringComparison.Ordinal) ? "different-file" : "original-file");
                TrainingGradeContext context = Context(workspace, templates, started, new DeclaredOutputSnapshot(baseline, current));
                TaskGradeOutcome expected = scenario == "new" || scenario == "hash-changed" || scenario == "replacement-changed" ||
                    scenario == "mtime-only" || scenario == "replacement-same-hash"
                    ? TaskGradeOutcome.Pass : TaskGradeOutcome.Fail;
                AssertOutcome(workspace, task, context, expected);
            }
        }

        public static IEnumerable<object[]> InvalidContexts()
        {
            foreach (string project in new[] { "Word2019_P09", "Word2019_P06", "Word2019_P07" })
                foreach (string problem in new[] { "no-context", "no-ownership", "no-entry", "unreadable", "blank-session", "default-start", "zero-baseline-sequence", "negative-current-sequence", "equal-sequence", "reversed-sequence", "wrong-path", "duplicate", "no-baseline", "no-current" })
                    yield return new object[] { project, problem };
        }

        [DataTestMethod]
        [DynamicData(nameof(InvalidContexts), DynamicDataSourceType.Method)]
        public void InvalidOutputObservationIsError(string projectId, string problem)
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = LoadTask(projectId, "T08");
                string templates = TemplateRoot(workspace);
                string path = ExpectedPath(workspace, templates, task);
                DateTime started = DateTime.UtcNow.AddSeconds(-2);
                OutputFileState baseline = ReadState(path, started, "original-file");
                File.WriteAllText(path, "new answer");
                OutputFileState current = ReadState(path, DateTime.UtcNow, "original-file");
                string session = Guid.NewGuid().ToString("N");
                if (problem == "blank-session") session = " ";
                if (problem == "zero-baseline-sequence") baseline = At(baseline, baseline.ObservedUtc, sequence: 0);
                if (problem == "negative-current-sequence") current = At(current, current.ObservedUtc, sequence: -1);
                if (problem == "equal-sequence") current = At(current, current.ObservedUtc, sequence: baseline.ObservationSequence);
                if (problem == "reversed-sequence") current = At(current, current.ObservedUtc, sequence: baseline.ObservationSequence - 1);
                if (problem == "wrong-path")
                {
                    baseline = At(baseline, baseline.ObservedUtc, path + ".wrong");
                    current = At(current, current.ObservedUtc, path + ".wrong");
                }
                var entry = new DeclaredOutputSnapshot(problem == "no-baseline" ? null : baseline,
                    problem == "no-current" ? null : current, problem == "unreadable" ? "Output could not be read: sharing violation." : null);
                var entries = new List<DeclaredOutputSnapshot>();
                if (problem != "no-entry") entries.Add(entry);
                if (problem == "duplicate") entries.Add(entry);
                var context = new TrainingGradeContext
                {
                    DefaultTemplateFolder = templates,
                    OutputOwnership = new OutputOwnershipSnapshot(session, problem == "default-start" ? default(DateTime) : started,
                        workspace.Folder, templates, entries)
                };
                if (problem == "no-ownership") context.OutputOwnership = null;
                AssertOutcome(workspace, task, problem == "no-context" ? null : context, TaskGradeOutcome.Error);
            }
        }

        [DataTestMethod]
        [DataRow("Word2019_P09", true)]
        [DataRow("Word2019_P09", false)]
        [DataRow("Word2019_P06", true)]
        [DataRow("Word2019_P07", true)]
        public void BackwardWallClockUsesSequenceAndRetainsStaleFail(string projectId, bool createNew)
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = LoadTask(projectId, "T08");
                string templates = TemplateRoot(workspace);
                string path = ExpectedPath(workspace, templates, task);
                if (!createNew) File.WriteAllText(path, "stale");
                DateTime started = DateTime.UtcNow;
                var baseline = ReadState(path, started.AddHours(-1));
                if (createNew) File.WriteAllText(path, "new output");
                var current = ReadState(path, started.AddHours(-2));
                AssertOutcome(workspace, task, Context(workspace, templates, started,
                    new DeclaredOutputSnapshot(baseline, current)), createNew ? TaskGradeOutcome.Pass : TaskGradeOutcome.Fail);
            }
        }

        [TestMethod]
        public void IdenticalOverwriteWithLaterWriteTimeIsFresh()
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = LoadTask("Word2019_P09", "T08");
                string templates = TemplateRoot(workspace);
                string path = ExpectedPath(workspace, templates, task);
                File.WriteAllText(path, "same bytes");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
                DateTime started = DateTime.UtcNow;
                var baseline = ReadState(path, started);
                File.WriteAllText(path, "same bytes");
                var current = ReadState(path, DateTime.UtcNow);
                Assert.AreEqual(baseline.Sha256, current.Sha256);
                Assert.AreEqual(baseline.Identity, current.Identity);
                Assert.IsTrue(current.LastWriteUtc > baseline.LastWriteUtc);
                AssertOutcome(workspace, task, Context(workspace, templates, started,
                    new DeclaredOutputSnapshot(baseline, current)), TaskGradeOutcome.Pass);
            }
        }

        [DataTestMethod]
        [DataRow("Word2019_P06")]
        [DataRow("Word2019_P07")]
        public void TemplateExistenceUsesObservedDefaultTemplateFolder(string projectId)
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = LoadTask(projectId, "T08");
                string templates = Path.Combine(workspace.Folder, "RedirectedWordTemplates");
                Directory.CreateDirectory(templates);
                string path = Path.Combine(templates, (string)task.Extra["expectedFileName"]);
                DateTime started = DateTime.UtcNow;
                OutputFileState baseline = ReadState(path, started);
                File.WriteAllText(path, "new template");
                AssertOutcome(workspace, task, Context(workspace, templates, started,
                    new DeclaredOutputSnapshot(baseline, ReadState(path, DateTime.UtcNow))), TaskGradeOutcome.Pass);
            }
        }

        [TestMethod]
        public void ExistenceGradingHonorsOwnerSnapshotContractWithoutRediscoveringFile()
        {
            // This tests the Core boundary, not end-to-end existence: the owner must capture immediately before grading.
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = LoadTask("Word2019_P09", "T08");
                string templates = TemplateRoot(workspace);
                string path = ExpectedPath(workspace, templates, task);
                DateTime started = DateTime.UtcNow;
                OutputFileState baseline = ReadState(path, started);
                File.WriteAllText(path, "new answer");
                var context = Context(workspace, templates, started, new DeclaredOutputSnapshot(baseline, ReadState(path, DateTime.UtcNow)));
                File.Delete(path);
                AssertOutcome(workspace, task, context, TaskGradeOutcome.Pass);
            }
        }

        [DataTestMethod]
        [DataRow("new", TaskGradeOutcome.Pass)]
        [DataRow("unchanged", TaskGradeOutcome.Fail)]
        [DataRow("hash-changed", TaskGradeOutcome.Pass)]
        [DataRow("wrong-content", TaskGradeOutcome.Fail)]
        [DataRow("wrong-name", TaskGradeOutcome.Fail)]
        [DataRow("not-current-document", TaskGradeOutcome.Fail)]
        [DataRow("no-ownership", TaskGradeOutcome.Error)]
        [DataRow("no-context", TaskGradeOutcome.Error)]
        public void SavedTemplateRequiresFreshnessAndExistingContentAndSaveAsRules(string scenario, TaskGradeOutcome expected)
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = LoadTask("Word2019_P20", "T01");
                string templates = TemplateRoot(workspace);
                string path = Path.Combine(templates, (string)task.Extra["expectedFileName"]);
                string good = TestCaseData.FixturePath("Word2019_P20/T01/pass.docx");
                if (scenario == "unchanged") File.Copy(good, path);
                if (scenario == "hash-changed") File.Copy(TestCaseData.FixturePath("Word2019_P20/T01/fail.docx"), path);
                DateTime started = DateTime.UtcNow;
                OutputFileState baseline = ReadState(path, started);
                if (scenario != "unchanged") File.Copy(scenario == "wrong-content"
                    ? TestCaseData.FixturePath("Word2019_P20/T01/fail.docx") : good, path, true);
                var context = Context(workspace, templates, started, new DeclaredOutputSnapshot(baseline, ReadState(path, DateTime.UtcNow)));
                context.SavedFromCurrentWorkingDocument = scenario != "not-current-document";
                if (scenario == "no-ownership") context.OutputOwnership = null;
                string gradedPath = path;
                if (scenario == "wrong-name")
                {
                    gradedPath = path + ".wrong.dotx";
                    File.Copy(good, gradedPath);
                }
                TaskGradeResult result = new WordGradingService(() => workspace.Folder)
                    .CheckTask(gradedPath, task, scenario == "no-context" ? null : context);
                Assert.AreEqual(expected, result.Outcome, result.Message);
                Assert.AreEqual(task.TaskId, result.TaskId);
                Assert.AreEqual(task.AssertionType, result.AssertionType);
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void SavedTemplateCannotBorrowFreshnessFromAnotherFolder(bool defaultFolderMatchesGradedDocument)
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = LoadTask("Word2019_P20", "T01");
                string observedFolder = TemplateRoot(workspace);
                string gradedFolder = Path.Combine(workspace.Folder, "OtherTemplates");
                Directory.CreateDirectory(gradedFolder);
                string name = (string)task.Extra["expectedFileName"];
                string observedPath = Path.Combine(observedFolder, name);
                string gradedPath = Path.Combine(gradedFolder, name);
                string good = TestCaseData.FixturePath("Word2019_P20/T01/pass.docx");
                // The valid template being graded predates this session; only the other path is fresh.
                File.Copy(good, gradedPath);
                DateTime started = DateTime.UtcNow;
                OutputFileState baseline = ReadState(observedPath, started);
                File.Copy(good, observedPath);
                var context = Context(workspace, observedFolder, started,
                    new DeclaredOutputSnapshot(baseline, ReadState(observedPath, DateTime.UtcNow)));
                context.DefaultTemplateFolder = defaultFolderMatchesGradedDocument ? gradedFolder : observedFolder;
                context.SavedFromCurrentWorkingDocument = true;
                TaskGradeResult result = new WordGradingService(() => workspace.Folder).CheckTask(gradedPath, task, context);
                Assert.AreEqual(TaskGradeOutcome.Fail, result.Outcome, result.Message);
                Assert.AreEqual(task.TaskId, result.TaskId);
                Assert.AreEqual(task.AssertionType, result.AssertionType);
            }
        }

        [DataTestMethod]
        [DataRow("new", TaskGradeOutcome.Pass)]
        [DataRow("unchanged", TaskGradeOutcome.Fail)]
        [DataRow("hash-changed", TaskGradeOutcome.Pass)]
        [DataRow("wrong-content", TaskGradeOutcome.Fail)]
        [DataRow("missing", TaskGradeOutcome.Fail)]
        [DataRow("no-ownership", TaskGradeOutcome.Error)]
        public void PlainTextExportRequiresFreshnessAndDeclaredContent(string scenario, TaskGradeOutcome expected)
        {
            using (var workspace = new TestWorkspace())
            {
                var task = new TaskDefinition { TaskId = "T01", AssertionType = "PlainTextDocumentExport" };
                task.Extra["expectedFileName"] = "Answer.txt";
                task.Extra["minimumCharacters"] = 10;
                task.Extra["requiredTextSegments"] = new JArray("Alpha");
                string path = Path.Combine(workspace.Folder, "Answer.txt");
                if (scenario == "unchanged") File.WriteAllText(path, "Alpha Beta Gamma");
                if (scenario == "hash-changed") File.WriteAllText(path, "old answer");
                DateTime started = DateTime.UtcNow;
                OutputFileState baseline = ReadState(path, started);
                if (scenario != "unchanged" && scenario != "missing")
                    File.WriteAllText(path, scenario == "wrong-content" ? "Beta Gamma Delta" : "Alpha Beta Gamma");
                var context = Context(workspace, TemplateRoot(workspace), started,
                    new DeclaredOutputSnapshot(baseline, ReadState(path, DateTime.UtcNow)));
                if (scenario == "no-ownership") context.OutputOwnership = null;
                AssertOutcome(workspace, task, context, expected);
            }
        }

        private static TaskDefinition LoadTask(string projectId, string taskId)
        {
            ProjectPackage package = new ProjectLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, projectId), "en");
            Assert.IsNotNull(package);
            return package.Tasks.Single(t => t.TaskId == taskId);
        }

        private static string TemplateRoot(TestWorkspace workspace)
        {
            string folder = Path.Combine(workspace.Folder, "Custom Office Templates");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string ExpectedPath(TestWorkspace workspace, string templates, TaskDefinition task)
        {
            return Path.Combine(task.AssertionType == "FileExistsInDocuments" ? workspace.Folder : templates,
                (string)task.Extra["expectedFileName"]);
        }

        private static TrainingGradeContext Context(TestWorkspace workspace, string templates, DateTime started,
            params DeclaredOutputSnapshot[] outputs)
        {
            return new TrainingGradeContext
            {
                DefaultTemplateFolder = templates,
                OutputOwnership = new OutputOwnershipSnapshot(Guid.NewGuid().ToString("N"), started,
                    workspace.Folder, templates, outputs)
            };
        }

        // Test-only file observation: no production ledger/filesystem implementation is involved.
        private static OutputFileState ReadState(string path, DateTime observed, string identity = "test-file")
        {
            if (!File.Exists(path)) return new OutputFileState(path, false, 0, null, default(DateTime), null, observed);
            var info = new FileInfo(path);
            string digest;
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            return new OutputFileState(path, true, info.Length, digest, info.LastWriteTimeUtc, identity, observed);
        }

        private static OutputFileState At(OutputFileState state, DateTime observed, string path = null, long? sequence = null)
        {
            return new OutputFileState(path ?? state.Path, state.Exists, state.Length, state.Sha256,
                state.LastWriteUtc, state.Identity, observed, sequence ?? state.ObservationSequence, state.CreatedUtc);
        }

        private static void AssertOutcome(TestWorkspace workspace, TaskDefinition task, TrainingGradeContext context, TaskGradeOutcome expected)
        {
            TaskGradeResult result = new WordGradingService(() => workspace.Folder).CheckTask(null, task, context);
            Assert.AreEqual(expected, result.Outcome, result.Message);
            Assert.AreEqual(task.TaskId, result.TaskId);
            Assert.AreEqual(task.AssertionType, result.AssertionType);
        }
    }
}
