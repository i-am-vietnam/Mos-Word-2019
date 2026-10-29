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
    public sealed class OutputSessionTests
    {
        [DataTestMethod]
        [DataRow("Word2019_P09")]
        [DataRow("Word2019_P07")]
        public void RestartCapturesANewBaselineAfterClosingThePrecedingAttempt(string projectId)
        {
            using (var workspace = new TestWorkspace())
            {
                var package = Load(projectId);
                var task = package.Tasks.Single(t => t.TaskId == "T08");
                string path = Path.Combine(workspace.Folder, (string)task.Extra["expectedFileName"]);
                File.WriteAllText(path, "personal pre-existing answer");
                var service = new TemplateOutputCleanupService(workspace.Folder, () => workspace.Folder, _ => { });
                service.Register(package); // The application calls this before OpenDocument on Go.
                string first = service.CurrentLedger.SessionId;
                Assert.AreEqual(TaskGradeOutcome.Fail, Grade(workspace, service, task).Outcome);
                File.WriteAllText(path, "changed during the first attempt");
                Assert.AreEqual(TaskGradeOutcome.Pass, Grade(workspace, service, task, path).Outcome);
                service.Cleanup(true); // The application has successfully closed the owned document.
                Assert.IsTrue(File.Exists(path), "Pre-existing output remains personal even after editing.");
                service.Register(package); // Restart must happen after close and before the next OpenDocument.
                Assert.AreNotEqual(first, service.CurrentLedger.SessionId);
                Assert.AreEqual(TaskGradeOutcome.Fail, Grade(workspace, service, task).Outcome,
                    "A preceding attempt's changed bytes must be stale against the Restart baseline.");
                service.Cleanup(true);
                Assert.IsTrue(File.Exists(path));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void P07CannotPassFromATemplateLeftByP20OrAnyEarlierSession(bool fromP20)
        {
            using (var workspace = new TestWorkspace())
            {
                string path = Path.Combine(workspace.Folder, "Notes.dotx");
                var messages = new List<string>();
                var service = new TemplateOutputCleanupService(workspace.Folder, () => workspace.Folder, messages.Add);
                if (fromP20)
                {
                    service.Register(Load("Word2019_P20"));
                    Assert.AreEqual(path, service.Capture().Outputs.Single().Baseline.Path);
                    File.Copy(TestCaseData.FixturePath("Word2019_P20/T01/pass.docx"), path);
                    service.ObserveOwnedDocument(path);
                    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        service.Cleanup(true); // A locked output remains instead of being forced away.
                    Assert.IsTrue(messages.Any(m => m.Contains("cleanup failed")));
                }
                else File.WriteAllText(path, "an earlier session's template");
                Assert.IsTrue(File.Exists(path));
                service.Register(Load("Word2019_P07"));
                var task = Load("Word2019_P07").Tasks.Single(t => t.TaskId == "T08");
                Assert.AreEqual(TaskGradeOutcome.Fail, Grade(workspace, service, task).Outcome);
                service.Cleanup(true);
                Assert.IsTrue(File.Exists(path), "Never acquire ownership of a pre-existing template.");
            }
        }

        [TestMethod]
        public void NewlySavedP20TemplateIsIncludedAndRemovedAfterClose()
        {
            using (var workspace = new TestWorkspace())
            {
                var package = Load("Word2019_P20");
                var task = package.Tasks.Single(t => t.TaskId == "T01");
                string path = Path.Combine(workspace.Folder, "Notes.dotx");
                var service = new TemplateOutputCleanupService(workspace.Folder, () => workspace.Folder, _ => { });
                service.Register(package);
                Assert.IsFalse(service.Capture().Outputs.Single().Baseline.Exists);
                File.Copy(TestCaseData.FixturePath("Word2019_P20/T01/pass.docx"), path);
                var context = new TrainingGradeContext
                {
                    DefaultTemplateFolder = workspace.Folder, SavedFromCurrentWorkingDocument = true,
                    OutputOwnership = service.Capture(path)
                };
                Assert.AreEqual(TaskGradeOutcome.Pass, new WordGradingService().CheckTask(path, task, context).Outcome);
                service.Cleanup();
                Assert.IsTrue(File.Exists(path), "No closure proof means no deletion.");
                service.Cleanup(true);
                Assert.IsFalse(File.Exists(path));
            }
        }

        [TestMethod]
        public void MemoIsNeverDeletedWhileItsOwnedDocumentIsLiveOrItsFileIsLocked()
        {
            using (var workspace = new TestWorkspace())
            {
                string path = Path.Combine(workspace.Folder, "Memo.txt");
                var messages = new List<string>();
                var service = new TemplateOutputCleanupService(workspace.Folder, () => workspace.Folder, messages.Add);
                service.Register(Load("Word2019_P09"));
                File.WriteAllText(path, "new memo saved by owned Word");
                service.ObserveOwnedDocument(path);
                service.Cleanup(false);
                Assert.IsTrue(File.Exists(path));
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    service.Cleanup(true);
                Assert.IsTrue(File.Exists(path));
                Assert.IsTrue(messages.Any(m => m.Contains("live")));
                Assert.IsTrue(messages.Any(m => m.Contains("cleanup failed")));
            }
        }

        [TestMethod]
        public void P16ArbitraryLearnerFolderIsOutsideDeclaredOutputCleanup()
        {
            using (var workspace = new TestWorkspace())
            {
                string documents = Path.Combine(workspace.Folder, "Documents");
                string learner = Path.Combine(workspace.Folder, "LearnerChosenFolder");
                Directory.CreateDirectory(learner);
                string path = Path.Combine(learner, "Outline for Proposal.txt");
                File.WriteAllText(path, "learner's export");
                var service = new TemplateOutputCleanupService(documents,
                    () => { Assert.Fail("P16 must not resolve or scan template folders."); return null; }, _ => { });
                var package = Load("Word2019_P16");
                service.Register(package);
                Assert.AreEqual(0, service.Capture(path).Outputs.Count);
                var task = package.Tasks.Single(t => t.TaskId == "T02");
                var context = new TrainingGradeContext { OwnedDocumentPath = path, OwnedDocumentSaveFormat = 2 };
                Assert.AreEqual(TaskGradeOutcome.Pass, new WordGradingService().CheckTask(null, task, context).Outcome);
                service.Cleanup(true);
                Assert.AreEqual("learner's export", File.ReadAllText(path));
                Assert.IsFalse(Directory.Exists(documents), "Do not create or scan unrelated declared roots.");
            }
        }

        [TestMethod]
        public void RegisterCannotOverwriteAnActiveAttemptsBaseline()
        {
            using (var workspace = new TestWorkspace())
            {
                var service = new TemplateOutputCleanupService(workspace.Folder, () => workspace.Folder, _ => { });
                service.Register(Load("Word2019_P09"));
                string session = service.CurrentLedger.SessionId;
                Assert.ThrowsException<InvalidOperationException>(() => service.Register(Load("Word2019_P09")));
                Assert.AreEqual(session, service.CurrentLedger.SessionId);
                service.Cleanup(true);
            }
        }

        private static ProjectPackage Load(string projectId) =>
            new ProjectLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, projectId), "en");

        private static TaskGradeResult Grade(TestWorkspace workspace, TemplateOutputCleanupService service,
            TaskDefinition task, string ownedPath = null) => new WordGradingService(() => workspace.Folder).CheckTask(null, task,
                new TrainingGradeContext { OutputOwnership = service.Capture(ownedPath) });
    }
}
