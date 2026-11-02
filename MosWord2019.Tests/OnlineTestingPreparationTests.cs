using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using MosWord2019.Projects.OnlinePackages;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class OnlineTestingPreparationTests
    {
        [TestMethod]
        public void OnlinePreparationUsesServerIdentityAndDefinitionWithoutCatalogOrComposer()
        {
            using (var lab = new Lab())
            {
                var prepared = lab.Store.PrepareOnline(lab.Online, lab.Packages, "en", new string('a', 64));
                Assert.AreEqual(lab.Online.Manifest.SessionId, Guid.Parse(prepared.SessionId));
                Assert.AreSame(lab.Online.ExamDefinition, prepared.Definition);
                Assert.IsNull(prepared.CatalogPath); Assert.IsTrue(prepared.IsOnline);
                Assert.IsTrue(prepared.ContentHashes.Count >= 14);
                Assert.AreEqual(lab.Online.Manifest.DefinitionHash, prepared.DefinitionHash);
                var data = JObject.Parse(File.ReadAllText(lab.Store.StatePath));
                Assert.AreEqual(new string('a', 64), (string)data["onlineMachineId"]);
                Assert.AreEqual(7, ((JObject)data["onlinePackages"]).Count);
                Assert.IsFalse(data.ToString().Contains("sessionToken"));
                var retry = lab.Store.PrepareOnline(lab.Online, lab.Packages, "vi");
                Assert.AreEqual("en", retry.Language);
                Assert.AreEqual(prepared.DefinitionHash, ExamDefinitionFingerprint.Compute(retry.Definition));
            }
        }
        [TestMethod]
        public void OnlineRecoveryKeepsSavedSnapshotAndPinnedDefinition()
        {
            using (var lab = new Lab())
            {
                var prepared = lab.Store.PrepareOnline(lab.Online, lab.Packages, "en");
                var clock = new TestingSessionTests.FakeClock();
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition, clock, new AlwaysOnlineMonitor()))
                { session.Start(); clock.Advance(42); lab.Store.Save(prepared, session, null); }
                var recovery = lab.Store.LoadOnlineSaved();
                Assert.AreEqual(TimeSpan.FromSeconds(42), recovery.Snapshot.Elapsed);
                Assert.AreEqual(prepared.DefinitionHash, recovery.Prepared.DefinitionHash);
                Assert.AreEqual(lab.Online.Manifest.SessionId, lab.Store.ReadOnlineSessionId());
                Assert.AreEqual(7, lab.Store.RecoveryPackages.Count);
                lab.Store.LoadOnline(lab.Online, lab.Packages);
                Assert.AreEqual(TimeSpan.FromSeconds(42), lab.Store.LoadOnlineSaved().Snapshot.Elapsed);
            }
        }
        [DataTestMethod]
        [DataRow("definition")]
        [DataRow("version")]
        [DataRow("identity")]
        [DataRow("seed")]
        public void ChangedOnlineResumeCannotReplacePinnedExam(string change)
        {
            using (var lab = new Lab())
            {
                lab.Store.PrepareOnline(lab.Online, lab.Packages, "en");
                string before = File.ReadAllText(lab.Store.StatePath);
                if (change == "definition") lab.Online.Manifest.DefinitionHash = new string('f', 64);
                if (change == "version") lab.Online.Manifest.Packages[0].Version = new string('e', 64);
                if (change == "identity") lab.Online.Manifest.SessionId = Guid.NewGuid();
                if (change == "seed")
                {
                    var data = JObject.Parse(before); data["seed"] = 999; File.WriteAllText(lab.Store.StatePath, data.ToString());
                    before = File.ReadAllText(lab.Store.StatePath);
                }
                Assert.ThrowsException<InvalidDataException>(() => lab.Store.PrepareOnline(lab.Online, lab.Packages, "en"));
                Assert.AreEqual(before, File.ReadAllText(lab.Store.StatePath));
            }
        }
        [DataTestMethod]
        [DataRow("tasks.json")]
        [DataRow("meta.json")]
        [DataRow("lang/en.json")]
        [DataRow("unexpected.txt")]
        public void RecoveryRejectsChangedPackageFilesAndRetainsState(string fileName)
        {
            using (var lab = new Lab())
            using (var copied = new TestWorkspace())
            {
                foreach (var package in lab.Packages)
                {
                    string root = Path.Combine(copied.Folder, package.Meta.ProjectId); Directory.CreateDirectory(root);
                    foreach (string file in Directory.GetFiles(package.ProjectFolderPath, "*", SearchOption.AllDirectories))
                    {
                        string target = Path.Combine(root, file.Substring(package.ProjectFolderPath.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target);
                    }
                }
                var packages = new ProjectLoader().LoadAll(copied.Folder, "en");
                var online = new OnlinePackageSession(lab.Online.Manifest, copied.Folder, null);
                lab.Store.PrepareOnline(online, packages, "en");
                string path = Path.Combine(packages[0].ProjectFolderPath, fileName); File.AppendAllText(path, " ");
                Assert.ThrowsException<InvalidDataException>(() => lab.Store.LoadOnlineSaved());
                Assert.IsTrue(File.Exists(lab.Store.StatePath));
            }
        }
        [TestMethod]
        public void RedownloadMovesRecoveryPathsWithoutRecomposingOrResettingElapsed()
        {
            using (var lab = new Lab())
            using (var moved = new TestWorkspace())
            {
                var prepared = lab.Store.PrepareOnline(lab.Online, lab.Packages, "en");
                var clock = new TestingSessionTests.FakeClock();
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition, clock, new AlwaysOnlineMonitor()))
                { session.Start(); clock.Advance(87); lab.Store.Save(prepared, session, null); }
                foreach (var package in lab.Packages)
                    foreach (string file in Directory.GetFiles(package.ProjectFolderPath, "*", SearchOption.AllDirectories))
                    {
                        string target = Path.Combine(moved.Folder, package.Meta.ProjectId, file.Substring(package.ProjectFolderPath.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target);
                    }
                var packages = new ProjectLoader().LoadAll(moved.Folder, "en");
                lab.Store.PrepareOnline(new OnlinePackageSession(lab.Online.Manifest, moved.Folder, null), packages, "en");
                Assert.IsTrue(lab.Store.RecoveryPackages.All(p => p.ProjectFolderPath.StartsWith(moved.Folder, StringComparison.OrdinalIgnoreCase)));
                var recovered = lab.Store.LoadOnlineSaved();
                Assert.AreEqual(TimeSpan.FromSeconds(87), recovered.Snapshot.Elapsed);
                Assert.AreEqual(prepared.DefinitionHash, recovered.Prepared.DefinitionHash);
            }
        }
        [TestMethod]
        public void PreparedDiscardRequiresSameOnlineIdentityAndCannotDiscardActiveExam()
        {
            using (var lab = new Lab())
            {
                var prepared = lab.Store.PrepareOnline(lab.Online, lab.Packages, "en");
                Assert.ThrowsException<InvalidOperationException>(() => lab.Store.DiscardPreparedOnline(Guid.NewGuid()));
                Assert.IsTrue(File.Exists(lab.Store.StatePath));
                lab.Store.DiscardPreparedOnline(lab.Online.Manifest.SessionId);
                Assert.IsFalse(File.Exists(lab.Store.StatePath));
                prepared = lab.Store.PrepareOnline(lab.Online, lab.Packages, "en");
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition,
                    new TestingSessionTests.FakeClock(), new AlwaysOnlineMonitor()))
                { session.Start(); lab.Store.Save(prepared, session, null); }
                Assert.ThrowsException<InvalidOperationException>(() => lab.Store.DiscardPreparedOnline(lab.Online.Manifest.SessionId));
                Assert.IsTrue(File.Exists(lab.Store.StatePath));
            }
        }
        [TestMethod]
        public void RecoveryPackageLeasePreservesCacheAndJournalUntilReleased()
        {
            using (var workspace = new TestWorkspace())
            {
                string cacheRoot = Path.Combine(workspace.Folder, "cache");
                string root = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
                string journalPath = Path.Combine(workspace.Folder, "journal.json");
                var originalJournal = new SessionRecoveryJournal(journalPath, _ => { });
                string path = Path.Combine(root, "Word2019_P01", "starter.docx");
                using (var originalLease = new PackageCacheLedger(root, originalJournal))
                {
                    originalLease.CreateDirectory(Path.GetDirectoryName(path));
                    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    { originalLease.ClaimEmptyFile(path, file); file.WriteByte(42); originalLease.CompleteFile(path, file); }
                    originalLease.RegisterCompleted(path);
                    // Releasing only the native lease models process loss, preserving the owned cache.
                }
                var restartedJournal = new SessionRecoveryJournal(journalPath, _ => { });
                using (OnlinePackageCache.RetainRecoveryPackages(root, restartedJournal))
                {
                    OnlinePackageCache.RecoverIncompletePackages(restartedJournal, cacheRoot);
                    Assert.IsTrue(File.Exists(path), "A resumed cache must survive package cleanup during redownload.");
                    restartedJournal.Recover(); Assert.IsTrue(File.Exists(path));
                    Assert.AreEqual(42, File.ReadAllBytes(path)[0]);
                }
                OnlinePackageCache.RecoverIncompletePackages(restartedJournal, cacheRoot);
                restartedJournal.Recover();
                Assert.IsFalse(File.Exists(path)); Assert.IsFalse(Directory.Exists(root)); Assert.AreEqual(0, restartedJournal.Count);
            }
        }
        [TestMethod]
        public void CompletedRecoveryReadsOwnedWordWithoutHydratingSourcePackages()
        {
            using (var lab = new Lab())
            {
                var prepared = lab.Store.PrepareOnline(lab.Online, lab.Packages, "en");
                var identity = new OwnedWordProcessIdentity(123, 100000, @"C:\Office\WINWORD.EXE");
                lab.Store.RecordOwnedWord(prepared, identity);
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition,
                    new TestingSessionTests.FakeClock(), new AlwaysOnlineMonitor()))
                {
                    session.Start(); session.BeginSubmit(new ExamTaskGrade[0]);
                    for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, new ExamTaskGrade[0]);
                    session.Complete(_ => { }); lab.Store.Save(prepared, session, null);
                }
                var state = JObject.Parse(File.ReadAllText(lab.Store.StatePath));
                state.Remove("recoveryPackages"); state.Remove("contentHashes"); File.WriteAllText(lab.Store.StatePath, state.ToString());
                Assert.AreEqual("Completed", lab.Store.PendingOnlineStatus);
                Assert.IsTrue(identity.Matches(lab.Store.ReadOwnedWord()));
                Assert.AreEqual(Environment.MachineName, lab.Store.ReadRecoveryMachine());
                Assert.IsTrue(File.Exists(lab.Store.StatePath));
            }
        }
        [DataTestMethod]
        [DataRow(2001)]
        [DataRow(2041)]
        public void OnlineRecoveryUsesAuthorityUtcForHeartbeatAndCrashDowntime(int authorityYear)
        {
            using (var lab = new Lab())
            {
                DateTime authorityNow = new DateTime(authorityYear, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                var store = new TestingSessionStateStore(lab.Store.StatePath, () => authorityNow);
                var prepared = store.PrepareOnline(lab.Online, lab.Packages, "en");
                var clock = new TestingSessionTests.FakeClock { Utc = authorityNow };
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition, clock, new AlwaysOnlineMonitor()))
                {
                    session.Start(); clock.Advance(42); authorityNow = clock.UtcNow;
                    store.Save(prepared, session, authorityNow);
                    var state = JObject.Parse(File.ReadAllText(store.StatePath));
                    Assert.AreEqual(authorityNow, (DateTime)state["lastHeartbeatUtc"]);
                    Assert.AreEqual(new DateTime(authorityYear, 1, 2, 3, 4, 5, DateTimeKind.Utc), (DateTime)state["startedUtc"]);
                    authorityNow = authorityNow.AddMinutes(2);
                    var record = store.LoadOnlineSaved();
                    Assert.AreEqual(clock.UtcNow, record.LastHeartbeatUtc);
                    var decision = record.Decide(authorityNow);
                    Assert.AreEqual(TimeSpan.FromMinutes(2), decision.Downtime);
                    Assert.AreEqual(TimeSpan.FromSeconds(162), decision.Elapsed);
                    Assert.AreEqual(TestingRecoveryAction.Resume, decision.Action);
                }
            }
        }
        private sealed class Lab : IDisposable
        {
            private readonly TestWorkspace folder = new TestWorkspace();
            internal readonly List<ProjectPackage> Packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en").Take(7).ToList();
            internal readonly OnlinePackageSession Online;
            internal readonly TestingSessionStateStore Store;
            internal Lab()
            {
                var definition = new ExamDefinition(123456, Packages.Select(p => new ExamProjectDefinition(p.Meta.ProjectId, p.Tasks.Take(5))));
                var manifest = new OnlinePackageManifest { SessionId = Guid.NewGuid(), Mode = "Testing", ExamDefinition = definition,
                    DefinitionHash = ExamDefinitionFingerprint.Compute(definition), Packages = Packages.Select(p => new OnlinePackageDescriptor
                    { Id = Guid.NewGuid(), Code = p.Meta.ProjectId, Version = new string('a', 64), Sha256 = new string('b', 64), ZipSha256 = new string('a', 64) }).ToList().AsReadOnly() };
                Online = new OnlinePackageSession(manifest, TestCaseData.ProjectsRoot, null);
                Store = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"));
            }
            public void Dispose() { folder.Dispose(); }
        }
    }
}
