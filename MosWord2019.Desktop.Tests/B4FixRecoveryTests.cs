using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using MosWord2019.Word;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class B4FixRecoveryTests
    {
        private const string SessionId = "8123ccd108534a2496b95b7b2cf831f3";
        private const string FixtureHash = "FA09E3BBCE37CAEDD5C5C6456771CDD1B24A6E73738BB931FC0439FE71FC3EA2";
        private static string Fixture => TestCaseData.FixturePath("B4Fix/testing-session-8123cc.json");

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RealFailingStateWithExitedOrUnreadablePidReachesResume(bool exited)
        {
            using (var folder = new TestWorkspace())
            {
                AssertFixtureHash();
                var store = CopyState(folder.Folder);
                var journal = new SessionRecoveryJournal(Path.Combine(folder.Folder, "journal.json"), _ => { });
                var data = JObject.Parse(File.ReadAllText(store.StatePath));
                int logs = 0;
                var process = new UnreadableProcess { HasExited = exited };
                var recovery = new OwnedWordProcessRecovery(_ => process, _ => logs++);
                bool prompted = false;
                var record = new TestingStartupRecovery(store, journal).Inspect(Catalog, Packages(),
                    ((DateTime)data["lastHeartbeatUtc"]).AddSeconds(5), recovery.IsRunning,
                    id => { Assert.Fail("Unproven/exited PID must not prompt for termination."); return true; }, recovery.Stop,
                    saved => { prompted = true; return TestingResumeChoice.Resume; }, saved => Assert.Fail("Resume was selected."));
                Assert.IsTrue(prompted); Assert.AreEqual(SessionId, record.Prepared.SessionId);
                Assert.AreEqual(exited ? 0 : 1, logs); Assert.AreEqual(exited ? 0 : 1, process.Reads);
                Assert.IsFalse(process.Terminated); Assert.IsTrue(store.RequiresRecovery);
                AssertFixtureHash();
            }
        }

        [TestMethod]
        public void RealFailingStateCanFinalizeFromIsolatedCheckpointWithoutActivatingWord()
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    AssertFixtureHash();
                    var store = CopyState(folder.Folder);
                    var packages = Packages();
                    var journal = new SessionRecoveryJournal(Path.Combine(folder.Folder, "journal.json"), _ => { });
                    string documents = Path.Combine(folder.Folder, "Documents"), templates = Path.Combine(folder.Folder, "Templates");
                    string sentinel = Path.Combine(folder.Folder, "retained-until-result.docx"); File.WriteAllText(sentinel, "last checkpoint evidence");
                    journal.Track(sentinel, "working", SessionId);
                    string work = Path.Combine(documents, "MosWord2019", "Working", "Testing", SessionId, "Word2019_P04", "work.docx");
                    Directory.CreateDirectory(Path.GetDirectoryName(work));
                    var positive = TestCaseData.LoadAll().First(c => c.ProjectId == "Word2019_P04" && c.TaskId == "T03" && c.Expected == "Pass");
                    File.Copy(TestCaseData.FixturePath(positive.Fixture), work);
                    string checkpoint = Path.Combine(Path.GetDirectoryName(work), "export-checkpoint.docx"); File.Copy(work, checkpoint);
                    var data = JObject.Parse(File.ReadAllText(store.StatePath));
                    var context = data["projectContexts"]["Word2019_P04"]["context"];
                    data["projectContexts"]["Word2019_P04"]["sourcePath"] = checkpoint;
                    context["DefaultTemplateFolder"] = templates; context["OwnedDocumentPath"] = work;
                    context["OutputOwnership"]["DocumentsFolder"] = documents;
                    File.WriteAllText(store.StatePath, data.ToString());
                    var word = new NeverOpenWord();
                    string resultPath = Path.Combine(folder.Folder, "Results", SessionId + ".json");
                    new TestingStartupRecovery(store, journal).FinalizeSaved(Catalog, packages, record =>
                    {
                        var clock = new StopwatchTestingClock();
                        var workspace = new TestingWorkspaceService(SessionId, journal, documents, Path.Combine(folder.Folder, "Pictures"), templates, _ => { }, true);
                        workspace.Preflight(record.Prepared, packages, false);
                        using (var session = record.Restore(clock, new AlwaysOnlineMonitor()))
                        using (var runner = new TestingProjectRunner(workspace, word, record.Prepared, store, folder.CreateGrader(documents), _ => { }))
                        using (var engine = new TestingSessionCoordinator(record.Prepared, session, store, clock, runner, () => Assert.Fail("No preflight/start during saved submission."), _ => { }))
                        {
                            engine.Completed += () => { Assert.IsTrue(File.Exists(resultPath)); Assert.IsTrue(File.Exists(sentinel)); };
                            engine.SubmitSaved();
                            Assert.AreEqual(TestingSessionStatus.Completed, session.Status);
                            Assert.IsTrue(session.Result.Total > 0, "The preserved positive checkpoint must be graded, not replaced by starter/zero.");
                        }
                    });
                    Assert.IsFalse(File.Exists(store.StatePath)); Assert.IsFalse(store.RequiresRecovery);
                    Assert.IsFalse(File.Exists(sentinel)); Assert.AreEqual(0, word.Activations);
                    var result = JObject.Parse(File.ReadAllText(resultPath));
                    Assert.AreEqual(SessionId, (string)result["sessionId"]); Assert.AreEqual(34, ((JArray)result["tasks"]).Count);
                    Assert.IsTrue((bool)result["pendingUpload"]); AssertFixtureHash();
                }
            });
        }

        private static string Catalog => Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json");
        private static System.Collections.Generic.IReadOnlyList<ProjectPackage> Packages() => new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "vi");
        private static TestingSessionStateStore CopyState(string folder)
        {
            string target = Path.Combine(folder, "testing-session.json"); File.Copy(Fixture, target);
            var data = JObject.Parse(File.ReadAllText(target)); data["machine"] = Environment.MachineName;
            File.WriteAllText(target, data.ToString()); // Adapt only the disposable copy for other test machines.
            return new TestingSessionStateStore(target);
        }
        private static void AssertFixtureHash()
        {
            using (var hash = SHA256.Create())
            using (var input = File.OpenRead(Fixture)) Assert.AreEqual(FixtureHash, BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""));
        }
        private static void Sta(Action action)
        {
            Exception failure = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private sealed class UnreadableProcess : IRecoveredWordProcess
        {
            public bool HasExited { get; set; }
            internal int Reads; internal bool Terminated;
            public OwnedWordProcessIdentity ReadIdentity() { Reads++; throw new Win32Exception(31); }
            public void Terminate() { Terminated = true; }
            public void Dispose() { }
        }
        private sealed class NeverOpenWord : IWordController, IWordDocumentState, IWordExportCheckpoint, IWordSaveEvents, IWordProcessIdentity
        {
            internal int Activations;
            public bool IsOpened => false;
            public string CurrentDocumentPath => throw new AssertFailedException("No live document during saved submission.");
            public string DefaultTemplateFolder => throw new AssertFailedException("No live Word preferences.");
            public OwnedWordProcessIdentity OwnedProcessIdentity => null;
            public event Action<OwnedWordProcessIdentity> OwnedProcessStarted { add { } remove { } }
            public event Action<string> OwnedDocumentSaving { add { } remove { } }
            public event Action<string> WorkingFileWritten { add { } remove { } }
            public event Action<string> ExportCheckpointFailed { add { } remove { } }
            public void StartWord() { Activations++; Assert.Fail("Saved finalization must not start Word."); }
            public void OpenDocument(string path) { Activations++; Assert.Fail("Saved finalization must not open Word."); }
            public void ReleaseClosedApplication() { Assert.Fail("No live application to retire."); }
            public void Save() { Assert.Fail("No live save during saved finalization."); }
            public void CloseDocument() { Assert.Fail("No live document to close."); }
            public void Close() { }
            public void Dispose() { }
            public void ConfigureExportCheckpoint(string path) { Assert.Fail("No checkpoint writes during saved submission."); }
            public string SaveFormattedCheckpoint() { Assert.Fail("No live XML reads during saved submission."); return null; }
        }
    }
}
