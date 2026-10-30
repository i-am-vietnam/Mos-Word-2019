using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TestingProjectRunnerTests
    {
        [DataTestMethod]
        [DataRow("Memo.txt")]
        [DataRow("Notes.dotx")]
        public void SaveAsThenLaterFormattingIsGradedFromLiveCheckpointWithoutSavingOutput(string name)
        {
            OnSta(() =>
            {
                using (var lab = new Lab())
                {
                    lab.Runner.Enter("Word2019_P25");
                    lab.Foreign(name, File.ReadAllBytes(lab.Word.CurrentDocumentPath));
                    byte[] original = File.ReadAllBytes(lab.Word.CurrentDocumentPath);
                    var fixture = JArray.Parse(File.ReadAllText(Path.Combine(TestCaseData.FixturesRoot, "a2-nearmiss.json")))
                        .Single(c => (string)c["projectId"] == "Word2019_P25" && (string)c["taskId"] == "T04");
                    lab.Word.Live = File.ReadAllBytes(TestCaseData.FixturePath((string)fixture["positiveFixture"])); // Later unsaved character spacing.
                    Assert.AreEqual(TaskGradeOutcome.Fail, lab.Grader.CheckTask(lab.Work.CurrentAttempt.WorkingPath,
                        lab.Work.CurrentAttempt.Package.Tasks.Single(t => t.TaskId == "T04")).Outcome);
                    lab.Runner.Autosave();
                    string output = lab.Word.CurrentDocumentPath;
                    var grades = lab.Runner.LeaveAndGrade();
                    Assert.AreEqual(TaskGradeOutcome.Pass, grades.Single(g => g.TaskId == "T04").Result.Outcome);
                    Assert.AreEqual(0, lab.Word.Saves); Assert.IsFalse(lab.Word.IsOpened);
                    CollectionAssert.AreEqual(original, File.ReadAllBytes(output));
                    Assert.IsTrue(lab.Word.CheckpointWrites >= 3);
                    lab.Work.CleanupCompleted(true);
                }
            });
        }
        [DataTestMethod]
        [DataRow("Word2019_P09", "T08", "Memo.txt")]
        [DataRow("Word2019_P20", "T01", "Notes.dotx")]
        public void DeclaredOutputIsGradedBeforeClosedDocumentCleanup(string project, string task, string name)
        {
            OnSta(() =>
            {
                using (var lab = new Lab())
                {
                    lab.Runner.Enter(project);
                    byte[] bytes = project == "Word2019_P20" ? File.ReadAllBytes(TestCaseData.FixturePath(TestCaseData.LoadAll()
                        .First(c => c.AssertionType == "SavedWordTemplate" && c.Expected == "Pass").Fixture)) : System.Text.Encoding.UTF8.GetBytes("owned text");
                    lab.Foreign(name, bytes);
                    string output = lab.Word.CurrentDocumentPath;
                    var result = lab.Runner.LeaveAndGrade().Single(g => g.TaskId == task);
                    Assert.AreEqual(TaskGradeOutcome.Pass, result.Result.Outcome, result.Result.Message);
                    Assert.AreEqual(0, lab.Word.Saves); Assert.IsFalse(File.Exists(output));
                    lab.Work.CleanupCompleted(true);
                }
            });
        }
        [TestMethod]
        public void ClosedOwnedWordReopensSavedWorkAndNormalSaveStaysOnWorkingCopy()
        {
            OnSta(() =>
            {
                using (var lab = new Lab())
                {
                    lab.Runner.Enter("Word2019_P25"); lab.Runner.Autosave(); Assert.AreEqual(1, lab.Word.Saves);
                    string path = lab.Word.CurrentDocumentPath; lab.Word.CloseDocument();
                    int reopened = 0; lab.Runner.DocumentReopened += () => reopened++;
                    lab.Runner.ReopenIfClosed(); Assert.AreEqual(1, reopened); Assert.AreEqual(path, lab.Word.CurrentDocumentPath);
                    lab.Runner.LeaveAndGrade(); lab.Work.CleanupCompleted(true);
                }
            });
        }
        [TestMethod]
        public void CorruptCheckpointDoesNotSaveForeignOutputOrReplaceWorkingCopy()
        {
            OnSta(() =>
            {
                using (var lab = new Lab())
                {
                    lab.Runner.Enter("Word2019_P25"); string work = lab.Word.CurrentDocumentPath;
                    byte[] original = File.ReadAllBytes(work);
                    lab.Foreign("Memo.txt", System.Text.Encoding.UTF8.GetBytes("exported text"));
                    lab.Word.Live = System.Text.Encoding.UTF8.GetBytes("invalid checkpoint");
                    Assert.ThrowsException<InvalidDataException>(() => lab.Runner.Autosave());
                    Assert.IsTrue(lab.Word.IsOpened); Assert.AreEqual(0, lab.Word.Saves);
                    CollectionAssert.AreEqual(original, File.ReadAllBytes(work));
                    lab.Word.CloseDocument(); lab.Work.CleanupCompleted(true);
                }
            });
        }

        [DataTestMethod]
        [DataRow("Word2019_P22", "T07", "Pass")]
        [DataRow("Word2019_P22", "T07", "Fail")]
        [DataRow("Word2019_P22", "T07", "Error")]
        [DataRow("Word2019_P24", "T03", "Pass")]
        [DataRow("Word2019_P24", "T03", "Fail")]
        [DataRow("Word2019_P24", "T03", "Error")]
        public void ExcludedSnapshotTasksAreReevaluatedOnlyInAnIsolatedTestCatalog(string project, string task, string expected)
        {
            OnSta(() =>
            {
                using (var lab = new Lab(project, task))
                {
                    lab.Runner.Enter(project);
                    if (project == "Word2019_P22")
                        lab.Word.Marks = expected == "Error" ? null : new WordFormattingMarksSnapshot(true, false, false, false, false, false, expected == "Fail");
                    else
                    {
                        var fixture = TestCaseData.LoadAll().Single(c => c.ProjectId == project && c.TaskId == task && c.Expected == "Pass");
                        lab.Word.Live = File.ReadAllBytes(TestCaseData.FixturePath(fixture.Fixture));
                        var layout = Newtonsoft.Json.JsonConvert.DeserializeObject<WordTocLayoutSnapshot>(File.ReadAllText(TestCaseData.FixturePath(fixture.TocLayout)));
                        lab.Word.Layout = expected == "Error" ? null : new WordTocLayoutSnapshot(lab.Word.CurrentDocumentPath,
                            layout.Targets.Select(t => new WordTocTargetSnapshot(t.Bookmark, t.Text, t.AdjustedPageNumber + (expected == "Fail" ? 100 : 0))));
                        lab.Foreign("Memo.txt", System.Text.Encoding.UTF8.GetBytes("exported text"));
                    }
                    var result = lab.Runner.LeaveAndGrade().Single(g => g.TaskId == task).Result;
                    Assert.AreEqual(expected, result.Outcome.ToString(), result.Message);
                    lab.Work.CleanupCompleted(true);
                }
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void LegacyStarterFailsUntilLearnerProducesModernSavedDocument(bool converted)
        {
            OnSta(() =>
            {
                using (var lab = new Lab("Word2019_P07", "T01"))
                {
                    lab.Runner.Enter("Word2019_P07");
                    if (converted)
                    {
                        var fixture = TestCaseData.LoadAll().Single(c => c.ProjectId == "Word2019_P07" && c.TaskId == "T01" && c.Expected == "Pass");
                        lab.Word.CurrentDocumentPath = Path.ChangeExtension(lab.Word.CurrentDocumentPath, ".docx");
                        lab.Word.Live = File.ReadAllBytes(TestCaseData.FixturePath(fixture.Fixture));
                        File.WriteAllBytes(lab.Word.CurrentDocumentPath, lab.Word.Live);
                    }
                    var result = lab.Runner.LeaveAndGrade().Single(g => g.TaskId == "T01").Result;
                    Assert.AreEqual(converted ? TaskGradeOutcome.Pass : TaskGradeOutcome.Fail, result.Outcome, result.Message);
                    lab.Work.CleanupCompleted(true);
                }
            });
        }
        [TestMethod]
        public void ReopenUsesLastNativeCheckpointEvenBeforeScheduledAutosave()
        {
            OnSta(() =>
            {
                using (var lab = new Lab())
                {
                    lab.Runner.Enter("Word2019_P25");
                    lab.Word.Live = File.ReadAllBytes(TestCaseData.FixturePath("Word2019_P25/T04/pass-a2.docx"));
                    lab.Word.SaveFormattedCheckpoint(); lab.Word.CloseDocument();
                    lab.Runner.ReopenIfClosed();
                    Assert.AreEqual(TaskGradeOutcome.Pass, lab.Runner.LeaveAndGrade().Single(g => g.TaskId == "T04").Result.Outcome);
                    lab.Work.CleanupCompleted(true);
                }
            });
        }

        [DataTestMethod]
        [DataRow("Word2019_P16", "T02")]
        [DataRow("Word2019_P20", "T01")]
        public void ReopeningPreservesObservedExportPathAndFormat(string project, string task)
        {
            OnSta(() =>
            {
                using (var lab = new Lab(project, task))
                {
                    lab.Runner.Enter(project);
                    string name = (string)lab.Work.CurrentAttempt.Package.Tasks.Single(t => t.TaskId == task).Extra["expectedFileName"];
                    byte[] bytes = project == "Word2019_P20" ? File.ReadAllBytes(TestCaseData.FixturePath(TestCaseData.LoadAll()
                        .First(c => c.AssertionType == "SavedWordTemplate" && c.Expected == "Pass").Fixture)) : System.Text.Encoding.UTF8.GetBytes("exported text");
                    lab.Foreign(name, bytes); lab.Word.CurrentSaveFormat = project == "Word2019_P16" ? 2 : 14;
                    string output = lab.Word.CurrentDocumentPath;
                    lab.Runner.ObserveAfterSave(); lab.Runner.Autosave(); lab.Word.CloseDocument();
                    lab.Runner.ReopenIfClosed();
                    Assert.AreEqual(TaskGradeOutcome.Pass, lab.Runner.LeaveAndGrade().Single(g => g.TaskId == task).Result.Outcome);
                    if (project == "Word2019_P16") CollectionAssert.AreEqual(bytes, File.ReadAllBytes(output));
                    lab.Work.CleanupCompleted(true);
                }
            });
        }
        [TestMethod]
        public void ObservedForeignLegacyConversionSurvivesCloseBeforeAutosave()
        {
            OnSta(() =>
            {
                using (var lab = new Lab("Word2019_P07", "T01"))
                {
                    lab.Runner.Enter("Word2019_P07");
                    var fixture = TestCaseData.LoadAll().Single(c => c.ProjectId == "Word2019_P07" && c.TaskId == "T01" && c.Expected == "Pass");
                    lab.Word.Live = File.ReadAllBytes(TestCaseData.FixturePath(fixture.Fixture));
                    lab.Foreign("learner.DOCX", lab.Word.Live); lab.Word.CurrentSaveFormat = 16;
                    lab.Runner.ObserveAfterSave(); lab.Word.SaveFormattedCheckpoint(); lab.Word.CloseDocument();
                    lab.Runner.ReopenIfClosed();
                    Assert.AreEqual(TaskGradeOutcome.Pass, lab.Runner.LeaveAndGrade().Single(g => g.TaskId == "T01").Result.Outcome);
                    lab.Work.CleanupCompleted(true);
                }
            });
        }

        [TestMethod]
        public void LearnerClosePreservesUnsavedFormattingBeforeReopen()
        {
            OnSta(() =>
            {
                using (var lab = new Lab())
                {
                    lab.Runner.Enter("Word2019_P25");
                    lab.Word.Live = File.ReadAllBytes(TestCaseData.FixturePath("Word2019_P25/T04/pass-a2.docx"));
                    using (var locked = new FileStream(lab.Work.CurrentAttempt.WorkingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) lab.Word.LearnerClose();
                    Assert.AreEqual(TaskGradeOutcome.Pass, lab.Grader.CheckTask(lab.Work.Files.GetExportCheckpointPath(lab.Work.CurrentAttempt.Package),
                        lab.Work.CurrentAttempt.Package.Tasks.Single(t => t.TaskId == "T04")).Outcome);
                    lab.Runner.ReopenIfClosed();
                    Assert.AreEqual(TaskGradeOutcome.Pass, lab.Runner.LeaveAndGrade().Single(g => g.TaskId == "T04").Result.Outcome);
                    lab.Work.CleanupCompleted(true);
                }
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ResumeRetainsOriginalOutputBaselineAndProtectsChangedClaim(bool changed)
        {
            OnSta(() =>
            {
                using (var lab = new Lab())
                {
                    lab.Runner.Enter("Word2019_P20");
                    var fixture = TestCaseData.LoadAll().First(c => c.AssertionType == "SavedWordTemplate" && c.Expected == "Pass");
                    lab.Foreign("Notes.dotx", File.ReadAllBytes(TestCaseData.FixturePath(fixture.Fixture)));
                    string output = lab.Word.CurrentDocumentPath; lab.Runner.Autosave(); lab.Word.CloseDocument(); lab.Runner.Dispose();
                    var journal = new SessionRecoveryJournal(Path.Combine(Path.GetDirectoryName(lab.Store.StatePath), "journal.json"), _ => { });
                    var work = new TestingWorkspaceService(lab.Prepared.SessionId, journal, Path.Combine(Path.GetDirectoryName(lab.Store.StatePath), "Documents"),
                        Path.Combine(Path.GetDirectoryName(lab.Store.StatePath), "Pictures"), Path.GetDirectoryName(output), _ => { }, true);
                    work.Preflight(lab.Prepared, lab.Packages, false);
                    var word = new FakeWord { DefaultTemplateFolder = Path.GetDirectoryName(output) };
                    using (var runner = new TestingProjectRunner(work, word, lab.Prepared, lab.Store, lab.Grader, _ => { }))
                    {
                        runner.Enter("Word2019_P20", true);
                        if (changed) File.AppendAllText(output, "changed after claim");
                        var grades = runner.LeaveAndGrade();
                        Assert.AreEqual(TaskGradeOutcome.Pass, grades.Single(g => g.TaskId == "T01").Result.Outcome); // ZIP permits trailing bytes; cleanup must still preserve the changed file.
                        Assert.AreEqual(changed, File.Exists(output));
                        work.CleanupCompleted(true);
                    }
                }
            });
        }
        [TestMethod]
        public void SavedGradingUsesUntouchedStarterForUnvisitedProject()
        {
            OnSta(() =>
            {
                using (var lab = new Lab())
                {
                    var grades = lab.Runner.GradeSaved("Word2019_P25");
                    Assert.IsTrue(grades.Count >= 4); Assert.IsTrue(grades.All(g => g.Result.Outcome == TaskGradeOutcome.Fail));
                    Assert.IsFalse(lab.Word.IsOpened); Assert.AreEqual(0, lab.Word.Saves);
                }
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void LegacyCloseCheckpointDoesNotAnswerConversionWithoutANativeModernSave(bool learnerSaves)
        {
            OnSta(() =>
            {
                using (var lab = new Lab("Word2019_P07", "T01"))
                {
                    lab.Runner.Enter("Word2019_P07");
                    var fixture = TestCaseData.LoadAll().Single(c => c.ProjectId == "Word2019_P07" && c.TaskId == "T01" && c.Expected == "Pass");
                    lab.Word.Live = File.ReadAllBytes(TestCaseData.FixturePath(fixture.Fixture));
                    lab.Word.LearnerClose(); lab.Runner.ReopenIfClosed();
                    if (learnerSaves) { lab.Word.Save(); lab.Runner.ObserveAfterSave(); }
                    var result = lab.Runner.LeaveAndGrade().Single(g => g.TaskId == "T01").Result;
                    Assert.AreEqual(learnerSaves ? TaskGradeOutcome.Pass : TaskGradeOutcome.Fail, result.Outcome, result.Message);
                    lab.Work.CleanupCompleted(true);
                }
            });
        }

        private static void OnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private sealed class Lab : IDisposable
        {
            private readonly TestWorkspace folder = new TestWorkspace();
            internal readonly FakeWord Word = new FakeWord();
            internal readonly TestingWorkspaceService Work;
            internal readonly TestingProjectRunner Runner;
            internal readonly WordGradingService Grader;
            private readonly string documents, templates;
            internal TestingSessionStateStore Store;
            internal PreparedTestingSession Prepared;
            internal System.Collections.Generic.IReadOnlyList<ProjectPackage> Packages;
            internal Lab(string extraProject = null, string extraTask = null)
            {
                documents = Path.Combine(folder.Folder, "Documents"); templates = Path.Combine(folder.Folder, "Templates");
                Directory.CreateDirectory(documents); Directory.CreateDirectory(templates); Word.DefaultTemplateFolder = templates;
                var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en");
                var catalog = JArray.Parse(File.ReadAllText(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json")));
                var chosen = new[] { "Word2019_P06", "Word2019_P09", "Word2019_P12", "Word2019_P20", "Word2019_P25", "Word2019_P26", "Word2019_P28" };
                if (extraProject == "Word2019_P07") chosen[3] = extraProject;
                else if (extraProject != null && !chosen.Contains(extraProject)) chosen[6] = extraProject;
                foreach (var entry in catalog)
                {
                    string id = (string)entry["projectId"]; entry["eligible"] = chosen.Contains(id);
                    if (id == extraProject)
                    { entry["requiredTaskIds"] = new JArray(extraTask); entry["excludedTaskIds"] = new JArray(); }
                    if (id == "Word2019_P25") entry["requiredTaskIds"] = new JArray("T04");
                    if (id == "Word2019_P09") entry["requiredTaskIds"] = new JArray("T08");
                    if (id == "Word2019_P20") entry["requiredTaskIds"] = new JArray("T01");
                }
                string path = Path.Combine(folder.Folder, "testing-catalog.json"); File.WriteAllText(path, catalog.ToString());
                var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "testing-session.json"));
                var prepared = store.Prepare(path, packages, "en"); Store = store; Prepared = prepared; Packages = packages.ToList();
                Work = new TestingWorkspaceService(prepared.SessionId, new SessionRecoveryJournal(Path.Combine(folder.Folder, "journal.json"), _ => { }),
                    documents, Path.Combine(folder.Folder, "Pictures"), templates, _ => { });
                Work.Preflight(prepared, packages); Grader = new WordGradingService(() => documents);
                Runner = new TestingProjectRunner(Work, Word, prepared, store, Grader, _ => { });
            }
            internal void Foreign(string name, byte[] bytes)
            {
                Word.CurrentDocumentPath = Path.Combine(name.EndsWith(".dotx", StringComparison.Ordinal) ? templates : documents, name);
                File.WriteAllBytes(Word.CurrentDocumentPath, bytes); Word.CurrentSaveFormat = name.EndsWith(".txt", StringComparison.Ordinal) ? 7 : 14;
            }
            public void Dispose() { Runner.Dispose(); folder.Dispose(); }
        }
        private sealed class FakeWord : IWordController, IWordDocumentState, IWordSaveFormat, IWordSaveEvents, IWordExportCheckpoint, IWordProcessIdentity, IWordCloseEvents, IWordViewState, IWordTocLayout
        {
            public bool IsOpened { get; private set; }
            public string CurrentDocumentPath { get; set; }
            public string DefaultTemplateFolder { get; set; }
            public int CurrentSaveFormat { get; set; } = 16;
            public OwnedWordProcessIdentity OwnedProcessIdentity { get; } = new OwnedWordProcessIdentity(123, 100000, @"C:\Office\WINWORD.EXE");
            public event Action<OwnedWordProcessIdentity> OwnedProcessStarted;
            public event Action<string> OwnedDocumentSaving;
            public event Action<string, int, string> OwnedDocumentClosing;
            internal void LearnerClose() { var saved = SaveFormattedCheckpoint(); OwnedDocumentClosing?.Invoke(CurrentDocumentPath, CurrentSaveFormat, saved); IsOpened = false; }
            public event Action<string> WorkingFileWritten;
            public event Action<string> ExportCheckpointFailed { add { } remove { } }
            internal WordFormattingMarksSnapshot Marks;
            internal WordTocLayoutSnapshot Layout;
            public WordFormattingMarksSnapshot ReadFormattingMarks() => Marks;
            public WordTocLayoutSnapshot ReadTocLayout() => Layout;
            internal byte[] Live; internal int Saves, CheckpointWrites; private string checkpoint;
            public void StartWord() { OwnedProcessStarted?.Invoke(OwnedProcessIdentity); }
            public void OpenDocument(string path) { CurrentDocumentPath = path; Live = File.ReadAllBytes(path); IsOpened = true; }
            public void Save() { OwnedDocumentSaving?.Invoke(CurrentDocumentPath); File.WriteAllBytes(CurrentDocumentPath, Live); Saves++; }
            public void CloseDocument() { IsOpened = false; }
            public void Close() { CloseDocument(); }
            public void Dispose() { }
            public void ConfigureExportCheckpoint(string path) { checkpoint = path; SaveFormattedCheckpoint(); }
            public string SaveFormattedCheckpoint() { File.WriteAllBytes(checkpoint, Live); CheckpointWrites++; WorkingFileWritten?.Invoke(checkpoint); return checkpoint; }
        }
    }
}
