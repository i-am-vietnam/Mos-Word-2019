using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TestingWorkspaceTests
    {
        [TestMethod]
        public void CryptographicSeedDefinitionAndFifteenAuditHashesAreDurableBeforeAnyWorkingCopy()
        {
            using (var lab = new Lab())
            {
                var json = JObject.Parse(File.ReadAllText(lab.Store.StatePath));
                Assert.AreEqual(lab.Prepared.Definition.Seed, (long)json["seed"]);
                Assert.AreEqual("Prepared", (string)json["status"]);
                Assert.AreEqual(15, lab.Prepared.ContentHashes.Count);
                Assert.AreEqual(7, ((JArray)json["definition"]["Projects"]).Count);
                Assert.AreEqual(lab.Prepared.SessionId, (string)json["sessionId"]);
                Assert.AreEqual(0L, (long)json["elapsedTicks"]);
                Assert.IsFalse(Directory.Exists(lab.Work.RootPath));
                Assert.ThrowsException<IOException>(() => lab.Store.Prepare(lab.Catalog, lab.Packages, "en"));
                var clock = new TestingSessionTests.FakeClock();
                using (var session = new TestingSession(lab.Prepared.SessionId, lab.Prepared.Definition, clock, new AlwaysOnlineMonitor()))
                {
                    session.Start(); clock.Advance(20);
                    lab.Store.Save(lab.Prepared, session, clock.UtcNow);
                    json = JObject.Parse(File.ReadAllText(lab.Store.StatePath));
                    Assert.AreEqual(TimeSpan.FromSeconds(20).Ticks, (long)json["elapsedTicks"]);
                    Assert.AreEqual("Running", (string)json["status"]);
                    Assert.AreEqual(clock.UtcNow, (DateTime)json["lastSaveUtc"]);
                    session.BeginSubmit(new ExamTaskGrade[0]);
                    for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, new ExamTaskGrade[0]);
                    session.Complete(_ => { }); lab.Store.Save(lab.Prepared, session, clock.UtcNow);
                    json = JObject.Parse(File.ReadAllText(lab.Store.StatePath));
                    Assert.AreEqual("Completed", (string)json["status"]);
                    Assert.AreEqual(0, (int)json["result"]["Total"]);
                    Assert.AreEqual(lab.Prepared.Definition.N, ((JArray)json["result"]["WrongTasks"]).Count);
                }
            }
        }

        [TestMethod]
        public void InvalidCatalogStillLeavesItsSeedAndCannotOverwriteThatRecord()
        {
            using (var folder = new TestWorkspace())
            {
                string catalog = Path.Combine(folder.Folder, "testing-catalog.json");
                File.WriteAllText(catalog, "{}");
                var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "testing-session.json"));
                Assert.ThrowsException<InvalidDataException>(() => store.Prepare(catalog, new ProjectPackage[0], "en"));
                var json = JObject.Parse(File.ReadAllText(store.StatePath));
                Assert.AreEqual("Preparing", (string)json["status"]);
                Assert.AreEqual(JTokenType.Integer, json["seed"].Type);
                Assert.ThrowsException<IOException>(() => store.Prepare(catalog, new ProjectPackage[0], "en"));
            }
        }

        [TestMethod]
        public void FailedAtomicSnapshotKeepsPreviousBytesAndRemovesOnlyItsTemporary()
        {
            using (var lab = new Lab())
            {
                byte[] prior = File.ReadAllBytes(lab.Store.StatePath);
                using (var locked = new FileStream(lab.Store.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.ThrowsException<IOException>(() => lab.Store.Save(lab.Prepared, null, null));
                CollectionAssert.AreEqual(prior, File.ReadAllBytes(lab.Store.StatePath));
                Assert.AreEqual(0, Directory.GetFiles(lab.Folder, ".testing-state-*.tmp").Length);
                lab.Store.Save(lab.Prepared, null, null);
                Assert.AreEqual("Prepared", (string)JObject.Parse(File.ReadAllText(lab.Store.StatePath))["status"]);
            }
        }

        [TestMethod]
        public void PreflightOnlyReadsThenEnterRestartLeaveReusePhaseAOwnershipAndAliases()
        {
            using (var lab = new Lab())
            {
                lab.Preflight();
                Assert.IsFalse(Directory.Exists(lab.Work.RootPath)); Assert.AreEqual(0, lab.Journal.Count);
                Assert.AreEqual(0, Directory.GetFiles(lab.Documents).Length);
                var attempt = lab.Work.Enter("Word2019_P26");
                string alias = Path.Combine(lab.Documents, "Coffee Sam.jpg");
                Assert.IsTrue(File.Exists(alias));
                Assert.AreEqual(Path.Combine(lab.Work.RootPath, "Word2019_P26", "work.docx"), attempt.WorkingPath);
                Assert.AreEqual(PreparedTestingSession.Hash(Path.Combine(attempt.Package.ProjectFolderPath, "assets", "Coffee_Sam.jpg")), PreparedTestingSession.Hash(alias));
                Assert.IsTrue(lab.Journal.Count >= 4);
                Assert.ThrowsException<InvalidOperationException>(() => lab.Work.Leave(false));
                Assert.ThrowsException<InvalidOperationException>(() => lab.Work.Restart(false));
                Assert.IsTrue(File.Exists(alias));
                File.WriteAllText(attempt.WorkingPath, "unsaved answer simulation");
                attempt = lab.Work.Restart(true);
                Assert.AreEqual(PreparedTestingSession.Hash(Path.Combine(attempt.Package.ProjectFolderPath, attempt.Package.Meta.Starter)), PreparedTestingSession.Hash(attempt.WorkingPath));
                lab.Work.Leave(true);
                Assert.IsFalse(File.Exists(alias)); Assert.IsTrue(File.Exists(attempt.WorkingPath));
                lab.Work.CleanupCompleted(true); Assert.IsFalse(File.Exists(attempt.WorkingPath));
                Assert.AreEqual(0, lab.Journal.Count);
            }
        }

        [DataTestMethod]
        [DataRow("asset")]
        [DataRow("picture")]
        [DataRow("memo")]
        [DataRow("notes")]
        [DataRow("cert")]
        public void PreflightRefusesForeignConflictsWithoutCopyingOrClaiming(string kind)
        {
            using (var lab = new Lab())
            {
                string file = kind == "asset" ? Path.Combine(lab.Documents, "Coffee Sam.jpg") :
                    kind == "picture" ? Path.Combine(lab.Pictures, "Map.png") :
                    kind == "memo" ? Path.Combine(lab.Documents, "Memo.txt") : Path.Combine(lab.Templates, kind == "notes" ? "Notes.dotx" : "Cert.dotx");
                File.WriteAllText(file, "personal content");
                Assert.ThrowsException<IOException>(() => lab.Preflight());
                Assert.AreEqual("personal content", File.ReadAllText(file));
                Assert.AreEqual(0, lab.Journal.Count); Assert.IsFalse(Directory.Exists(lab.Work.RootPath));
                Assert.ThrowsException<InvalidOperationException>(() => lab.Work.Enter("Word2019_P26"));
            }
        }

        [TestMethod]
        public void IdenticalPreexistingAssetIsReusedAndPreservedWhileNewCopiesAreRemoved()
        {
            using (var lab = new Lab())
            {
                string asset = Path.Combine(lab.Documents, "Coffee Sam.jpg");
                File.Copy(Path.Combine(lab.Packages.Single(p => p.Meta.ProjectId == "Word2019_P26").ProjectFolderPath, "assets", "Coffee_Sam.jpg"), asset);
                lab.Preflight(); lab.Work.Enter("Word2019_P26"); lab.Work.Leave(true);
                Assert.IsTrue(File.Exists(asset));
                Assert.IsFalse(File.Exists(Path.Combine(lab.Documents, "Coffee_Sam.jpg")));
                lab.Work.CleanupCompleted(true);
            }
        }

        [TestMethod]
        public void DeclaredOutputBaselinePrecedesEntryAndRestartAndCleanupRequiresClose()
        {
            using (var lab = new Lab())
            {
                lab.Preflight(); var attempt = lab.Work.Enter("Word2019_P20");
                var baseline = attempt.Outputs.Capture().Outputs.Single().Baseline;
                Assert.IsFalse(baseline.Exists);
                string output = Path.Combine(lab.Templates, "Notes.dotx");
                File.WriteAllText(output, "owned output");
                attempt.Outputs.ObserveOwnedDocument(output);
                Assert.IsTrue(attempt.Outputs.Capture(output).Outputs.Single().Current.Exists);
                Assert.ThrowsException<InvalidOperationException>(() => lab.Work.Leave(false));
                Assert.IsTrue(File.Exists(output));
                var restarted = lab.Work.Restart(true);
                Assert.IsFalse(File.Exists(output));
                Assert.IsFalse(restarted.Outputs.Capture().Outputs.Single().Baseline.Exists);
                Assert.IsTrue(restarted.Outputs.Capture().Outputs.Single().Baseline.ObservationSequence > baseline.ObservationSequence);
                lab.Work.CleanupCompleted(true);
            }
        }

        [TestMethod]
        public void OutputAppearingAfterPreflightIsProtectedAtEntry()
        {
            using (var lab = new Lab())
            {
                lab.Preflight(); string output = Path.Combine(lab.Templates, "Notes.dotx"); File.WriteAllText(output, "new personal output");
                Assert.ThrowsException<IOException>(() => lab.Work.Enter("Word2019_P20"));
                Assert.AreEqual("new personal output", File.ReadAllText(output)); Assert.AreEqual(0, lab.Journal.Count);
            }
        }

        [TestMethod]
        public void SessionRootsAndCleanupAreIndependent()
        {
            using (var lab = new Lab())
            {
                var secondStore = new TestingSessionStateStore(Path.Combine(lab.Folder, "second-state.json"));
                var prepared = secondStore.Prepare(lab.Catalog, lab.Packages, "vi");
                var second = new TestingWorkspaceService(prepared.SessionId, lab.Journal, lab.Documents, lab.Pictures, lab.Templates, _ => { });
                lab.Preflight(); second.Preflight(prepared, lab.Packages);
                var one = lab.Work.Enter("Word2019_P25"); var two = second.Enter("Word2019_P25");
                Assert.AreNotEqual(one.WorkingPath, two.WorkingPath);
                lab.Work.CleanupCompleted(true); Assert.IsTrue(File.Exists(two.WorkingPath));
                second.CleanupCompleted(true); Assert.AreEqual(0, lab.Journal.Count);
            }
        }

        [TestMethod]
        public void CatalogChangedAfterPreparationCannotPassPreflight()
        {
            using (var lab = new Lab())
            {
                lab.Preflight();
                File.AppendAllText(lab.Catalog, " ");
                Assert.ThrowsException<InvalidDataException>(() => lab.Work.Enter("Word2019_P25"));
                Assert.ThrowsException<InvalidDataException>(() => lab.Preflight());
                Assert.ThrowsException<InvalidOperationException>(() => lab.Work.Enter("Word2019_P25"));
                Assert.AreEqual(0, lab.Journal.Count);
            }
        }

        [DataTestMethod]
        [DataRow("tasks.json")]
        [DataRow("starter.docx")]
        public void ChangedAuditedPackageCannotPassPreflight(string name)
        {
            using (var lab = new Lab(true))
            {
                var package = lab.Packages.Single(p => p.Meta.ProjectId == "Word2019_P25");
                File.AppendAllText(Path.Combine(package.ProjectFolderPath, name), " ");
                Assert.ThrowsException<InvalidDataException>(() => lab.Preflight());
                Assert.AreEqual(0, lab.Journal.Count); Assert.IsFalse(Directory.Exists(lab.Work.RootPath));
            }
        }

        [TestMethod]
        public void WrongSnapshotSessionAndNonUtcSaveTimePreserveState()
        {
            using (var lab = new Lab())
            using (var other = new TestingSession(Guid.NewGuid().ToString("N"), lab.Prepared.Definition,
                new StopwatchTestingClock(), new AlwaysOnlineMonitor()))
            {
                byte[] before = File.ReadAllBytes(lab.Store.StatePath);
                Assert.ThrowsException<InvalidDataException>(() => lab.Store.Save(lab.Prepared, other, null));
                Assert.ThrowsException<ArgumentException>(() => lab.Store.Save(lab.Prepared, null, DateTime.Now));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(lab.Store.StatePath));
                Directory.CreateDirectory(lab.Work.RootPath);
                Assert.ThrowsException<IOException>(() => new TestingWorkspaceService(lab.Prepared.SessionId, lab.Journal,
                    lab.Documents, lab.Pictures, lab.Templates, _ => { }));
            }
        }

        private sealed class Lab : IDisposable
        {
            private readonly TestWorkspace workspace = new TestWorkspace();
            internal string Folder => workspace.Folder;
            internal string Documents, Pictures, Templates, Catalog;
            internal List<ProjectPackage> Packages;
            internal TestingSessionStateStore Store;
            internal PreparedTestingSession Prepared;
            internal TestingWorkspaceService Work;
            internal SessionRecoveryJournal Journal;
            internal Lab(bool copyP25 = false)
            {
                Documents = Path.Combine(Folder, "Documents"); Pictures = Path.Combine(Folder, "Pictures"); Templates = Path.Combine(Folder, "Templates");
                foreach (string directory in new[] { Documents, Pictures, Templates }) Directory.CreateDirectory(directory);
                Packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en").ToList();
                if (copyP25)
                {
                    var source = Packages.Single(p => p.Meta.ProjectId == "Word2019_P25");
                    string target = Path.Combine(Folder, "Word2019_P25");
                    foreach (string file in Directory.GetFiles(source.ProjectFolderPath, "*", SearchOption.AllDirectories))
                    {
                        string destination = Path.Combine(target, file.Substring(source.ProjectFolderPath.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(file, destination);
                    }
                    Packages[Packages.IndexOf(source)] = new ProjectLoader().Load(target, "en");
                }
                var catalog = JArray.Parse(File.ReadAllText(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json")));
                var chosen = new HashSet<string> { "Word2019_P06", "Word2019_P09", "Word2019_P12", "Word2019_P20", "Word2019_P24", "Word2019_P25", "Word2019_P26" };
                foreach (var entry in catalog) entry["eligible"] = chosen.Contains((string)entry["projectId"]);
                Catalog = Path.Combine(Folder, "testing-catalog.json"); File.WriteAllText(Catalog, catalog.ToString());
                Store = new TestingSessionStateStore(Path.Combine(Folder, "testing-session.json"));
                Prepared = Store.Prepare(Catalog, Packages, "en");
                Journal = new SessionRecoveryJournal(Path.Combine(Folder, "journal.json"), _ => { });
                Work = new TestingWorkspaceService(Prepared.SessionId, Journal, Documents, Pictures, Templates, _ => { });
            }
            internal void Preflight() => Work.Preflight(Prepared, Packages);
            public void Dispose() { workspace.Dispose(); }
        }
    }
}
