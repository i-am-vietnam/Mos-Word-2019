using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TestingRecoveryTests
    {
        [DataTestMethod]
        [DataRow(0, 899, true)]
        [DataRow(0, 900, true)]
        [DataRow(0, 901, false)]
        [DataRow(2400, 600, false)]
        [DataRow(2399, 600, true)]
        public void DowntimeCountsAndResumeHasExactFifteenMinuteBoundary(int elapsed, int downtime, bool resume)
        {
            DateTime now = DateTime.UtcNow;
            foreach (var status in new[] { TestingSessionStatus.Running, TestingSessionStatus.Offline })
            {
                var decision = TestingRecoveryDecision.Evaluate(status, TimeSpan.FromSeconds(elapsed), now.AddSeconds(-downtime), now, true);
                Assert.AreEqual(resume ? TestingRecoveryAction.Resume : TestingRecoveryAction.Finalize, decision.Action);
                Assert.AreEqual(TimeSpan.FromSeconds(Math.Min(3000, elapsed + downtime)), decision.Elapsed);
            }
        }
        [TestMethod]
        public void MachineMismatchSubmittingAndBackwardsUtcAreSafe()
        {
            var now = DateTime.UtcNow;
            Assert.AreEqual(TestingRecoveryAction.Finalize, TestingRecoveryDecision.Evaluate(TestingSessionStatus.Running, TimeSpan.Zero, now, now, false).Action);
            Assert.AreEqual(TestingRecoveryAction.Finalize, TestingRecoveryDecision.Evaluate(TestingSessionStatus.Submitting, TimeSpan.Zero, now, now, true).Action);
            Assert.AreEqual(TimeSpan.FromMinutes(2), TestingRecoveryDecision.Evaluate(TestingSessionStatus.Running, TimeSpan.FromMinutes(2), now.AddMinutes(1), now, true).Elapsed);
            Assert.AreEqual(TestingRecoveryAction.StartPrepared, TestingRecoveryDecision.Evaluate(TestingSessionStatus.Ready, TimeSpan.Zero, default(DateTime), now, true).Action);
        }
        [TestMethod]
        public void PreparedRetryReusesSeedAndDefinitionAndCompletedStateDoesNotBlockNewExam()
        {
            using (var lab = new Lab())
            {
                var retry = lab.Store.Prepare(lab.Catalog, lab.Packages, "vi");
                Assert.AreEqual(lab.Prepared.SessionId, retry.SessionId);
                Assert.IsTrue(JToken.DeepEquals(JToken.FromObject(lab.Prepared.Definition), JToken.FromObject(retry.Definition)));
                Assert.AreEqual("en", retry.Language);
                using (var session = lab.Session())
                {
                    session.Start(); session.BeginSubmit(new ExamTaskGrade[0]);
                    for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, new ExamTaskGrade[0]);
                    session.Complete(_ => { }); lab.Store.Save(lab.Prepared, session, null);
                }
                var next = lab.Store.Prepare(lab.Catalog, lab.Packages, "en");
                Assert.AreNotEqual(lab.Prepared.SessionId, next.SessionId);
                Assert.IsTrue(File.Exists(Path.Combine(lab.Folder, "Results", lab.Prepared.SessionId + ".json")));
            }
        }
        [TestMethod]
        public void LoadRestoresLockedGradesAndAddsCrashTimeBeforeClockRunsAgain()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                session.Start(); lab.Clock.Advance(40);
                var first = session.Definition.Projects[0];
                session.NextProject(first.TaskIds.Select(id => new ExamTaskGrade(first.ProjectId, id, new TaskGradeResult(TaskGradeOutcome.Pass, "", "test", id))));
                lab.Store.Save(lab.Prepared, session, lab.Clock.UtcNow);
                var json = JObject.Parse(File.ReadAllText(lab.Store.StatePath)); json["lastHeartbeatUtc"] = lab.Clock.UtcNow;
                File.WriteAllText(lab.Store.StatePath, json.ToString()); lab.Clock.Advance(60);
                var loaded = lab.Store.Load(lab.Catalog, lab.Packages);
                using (var restored = loaded.Restore(lab.Clock, new AlwaysOnlineMonitor()))
                {
                    Assert.AreEqual(TimeSpan.FromSeconds(100), restored.Elapsed);
                    Assert.AreEqual(1, restored.CurrentIndex); Assert.AreEqual(first.TaskIds.Count, restored.GetLockedResults().Count);
                    lab.Clock.Advance(10); Assert.AreEqual(TimeSpan.FromSeconds(110), restored.Elapsed);
                }
            }
        }
        [TestMethod]
        public void HeartbeatPersistsEveryFiveSecondsWithoutChangingLastAutosaveOrOwnedIdentity()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            using (var heartbeat = new TestingHeartbeat(lab.Prepared, session, lab.Store, lab.Clock, false))
            {
                var identity = new OwnedWordProcessIdentity(123, 100000, @"C:\Office\WINWORD.EXE");
                lab.Store.RecordOwnedWord(lab.Prepared, identity); session.Start();
                lab.Store.Save(lab.Prepared, session, lab.Clock.UtcNow);
                string before = File.ReadAllText(lab.Store.StatePath);
                lab.Clock.Advance(4); heartbeat.Pulse(); Assert.AreEqual(before, File.ReadAllText(lab.Store.StatePath));
                lab.Clock.Advance(1); heartbeat.Pulse();
                var json = JObject.Parse(File.ReadAllText(lab.Store.StatePath));
                Assert.AreEqual(TimeSpan.FromSeconds(5).Ticks, (long)json["elapsedTicks"]);
                Assert.AreEqual(lab.Clock.UtcNow.AddSeconds(-5), (DateTime)json["lastSaveUtc"]);
                Assert.IsTrue(identity.Matches(lab.Store.Load(lab.Catalog, lab.Packages).OwnedWord));
            }
        }
        [TestMethod]
        public void TaggedWorkingAssetAndOutputClaimsCanBePreservedThenRecovered()
        {
            using (var lab = new Lab())
            {
                var journal = new SessionRecoveryJournal(Path.Combine(lab.Folder, "journal.json"), _ => { });
                var files = new TrainingWorkspaceService(Path.Combine(lab.Folder, "Working"), lab.Folder, lab.Folder, journal, lab.Prepared.SessionId);
                var asset = lab.Packages.Single(p => p.Meta.ProjectId == "Word2019_P26");
                files.PrepareFreshWorkingCopy(asset); files.StageProjectAssetsToDocuments(asset);
                var claims = (JArray)JObject.Parse(File.ReadAllText(Path.Combine(lab.Folder, "journal.json")))["entries"];
                Assert.IsTrue(claims.Count >= 4 && claims.All(c => (string)c["sessionId"] == lab.Prepared.SessionId));
                journal.PreserveSession(lab.Prepared.SessionId); journal.Recover(); Assert.AreEqual(claims.Count, journal.Count);
                journal.ReleaseSession(lab.Prepared.SessionId); journal.Recover(); Assert.AreEqual(0, journal.Count);
            }
        }
        [TestMethod]
        public void StartupGuardDefersRecoveryForActiveOrCorruptStateAndArchivesCompletedOnlyAfterResultExists()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                var journal = new SessionRecoveryJournal(Path.Combine(lab.Folder, "journal.json"), _ => { });
                string file = Path.Combine(lab.Folder, "owned.txt"); File.WriteAllText(file, "owned"); journal.Track(file, "output", lab.Prepared.SessionId);
                session.Start(); lab.Store.Save(lab.Prepared, session, null);
                lab.Store.ProtectPendingRecovery(journal); journal.Recover(); Assert.IsTrue(File.Exists(file));
                File.WriteAllText(lab.Store.StatePath, "broken"); lab.Store.ProtectPendingRecovery(journal); journal.Recover(); Assert.IsTrue(File.Exists(file));
                journal.DeferRecovery(false); journal.ReleaseSession(lab.Prepared.SessionId); journal.Recover(); Assert.IsFalse(File.Exists(file));
            }
        }
        [TestMethod]
        public void OwnedWordStartRegistrationPersistsIdentityAndUnsubscribes()
        {
            using (var lab = new Lab())
            {
                var source = new FakeIdentitySource();
                using (var registration = new TestingWordProcessRegistration(source, lab.Prepared, lab.Store))
                    source.Start(new OwnedWordProcessIdentity(123, 100000, @"C:\Office\WINWORD.EXE"));
                Assert.AreEqual(123, lab.Store.Load(lab.Catalog, lab.Packages).OwnedWord.ProcessId);
                source.Start(new OwnedWordProcessIdentity(124, 100001, @"C:\Office\WINWORD.EXE"));
                Assert.AreEqual(123, lab.Store.Load(lab.Catalog, lab.Packages).OwnedWord.ProcessId);
            }
        }
        [DataTestMethod]
        [DataRow(2399, false)]
        [DataRow(2400, true)]
        public void RestoredTimerFinalizesImmediatelyWhenCrashConsumesRemainingTime(int seconds, bool expired)
        {
            using (var lab = new Lab())
            using (var original = lab.Session())
            {
                original.Start(); lab.Clock.Advance(seconds); lab.Store.Save(lab.Prepared, original, null);
                var data = JObject.Parse(File.ReadAllText(lab.Store.StatePath)); data["lastHeartbeatUtc"] = lab.Clock.UtcNow;
                File.WriteAllText(lab.Store.StatePath, data.ToString()); lab.Clock.Advance(600);
                var record = lab.Store.Load(lab.Catalog, lab.Packages);
                using (var restored = record.Restore(lab.Clock, new AlwaysOnlineMonitor()))
                {
                    Assert.AreEqual(expired, restored.InputLocked);
                    Assert.AreEqual(TimeSpan.FromSeconds(expired ? 0 : 1), restored.Remaining);
                    if (expired) Assert.AreEqual(TestingFinalizeReason.Timeout, restored.FinalizeReason);
                }
            }
        }
        [DataTestMethod]
        [DataRow("negative")]
        [DataRow("index")]
        [DataRow("states")]
        [DataRow("grade")]
        public void MalformedResumeStateIsRejectedWithoutChangingIt(string kind)
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                session.Start(); lab.Store.Save(lab.Prepared, session, null);
                var data = JObject.Parse(File.ReadAllText(lab.Store.StatePath));
                if (kind == "negative") data["elapsedTicks"] = -1;
                if (kind == "index") data["currentIndex"] = 7;
                if (kind == "states") data["projectStates"][0] = 0;
                if (kind == "grade") data["lockedResults"] = new JArray(new JObject { ["ProjectId"] = "Word2019_P01", ["TaskId"] = "T01", ["Result"] = new JObject() });
                File.WriteAllText(lab.Store.StatePath, data.ToString()); string before = File.ReadAllText(lab.Store.StatePath);
                Assert.ThrowsException<InvalidDataException>(() => lab.Store.Load(lab.Catalog, lab.Packages));
                Assert.AreEqual(before, File.ReadAllText(lab.Store.StatePath));
            }
        }

        [DataTestMethod]
        [DataRow(119, false)]
        [DataRow(120, false)]
        [DataRow(121, true)]
        public void ClockRollbackIsFlaggedWithoutGrantingNegativeDowntime(int seconds, bool suspicious)
        {
            var now = DateTime.UtcNow;
            var decision = TestingRecoveryDecision.Evaluate(TestingSessionStatus.Running, TimeSpan.FromMinutes(10), now, now.AddSeconds(-seconds), true);
            Assert.AreEqual(TimeSpan.Zero, decision.Downtime);
            var flag = typeof(TestingRecoveryDecision).GetProperty("SuspiciousClockRollback");
            Assert.IsNotNull(flag, "Recovery must expose the suspicious rollback for persistence.");
            Assert.AreEqual(suspicious, flag.GetValue(decision));
        }

        [TestMethod]
        public void DeclinedOrphanConfirmationKeepsSessionAndJournalPendingForNextStart()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                session.Start(); lab.Store.Save(lab.Prepared, session, null);
                lab.Store.RecordOwnedWord(lab.Prepared, new OwnedWordProcessIdentity(123, 100000, @"C:\Office\WINWORD.EXE"));
                var journal = new SessionRecoveryJournal(Path.Combine(lab.Folder, "journal.json"), _ => { });
                string owned = Path.Combine(lab.Folder, "owned.docx"); File.WriteAllText(owned, "preserve"); journal.Track(owned, "working", lab.Prepared.SessionId);
                var startup = new TestingStartupRecovery(lab.Store, journal); int prompts = 0, stops = 0, finalized = 0;
                Func<bool, TestingRecoveryRecord> inspect = accept => startup.Inspect(lab.Catalog, lab.Packages, DateTime.UtcNow,
                    (id, machine) => true, id => { prompts++; return accept; }, (id, machine) => { stops++; return true; },
                    record => TestingResumeChoice.Resume, record => finalized++);
                Assert.IsNull(inspect(false)); journal.Recover(); Assert.IsTrue(File.Exists(owned));
                Assert.AreEqual(0, stops); Assert.AreEqual(0, finalized);
                Assert.IsNotNull(inspect(true)); Assert.AreEqual(2, prompts); Assert.AreEqual(1, stops); journal.Recover(); Assert.IsTrue(File.Exists(owned));
            }
        }
        [TestMethod]
        public void ExpiredStartupFinalizesBeforeRecoveringAnyAutosave()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                session.Start(); lab.Store.Save(lab.Prepared, session, null);
                var state = JObject.Parse(File.ReadAllText(lab.Store.StatePath)); state["lastHeartbeatUtc"] = DateTime.UtcNow.AddMinutes(-16); File.WriteAllText(lab.Store.StatePath, state.ToString());
                var journal = new SessionRecoveryJournal(Path.Combine(lab.Folder, "journal.json"), _ => { });
                string owned = Path.Combine(lab.Folder, "work.docx"); File.WriteAllText(owned, "last autosave"); journal.Track(owned, "working", lab.Prepared.SessionId);
                bool finalized = false;
                new TestingStartupRecovery(lab.Store, journal).Inspect(lab.Catalog, lab.Packages, DateTime.UtcNow,
                    (id, machine) => false, id => false, (id, machine) => false, record => { Assert.Fail("Expired sessions cannot resume."); return TestingResumeChoice.Resume; },
                    record =>
                    {
                        Assert.IsTrue(File.Exists(owned));
                        using (var restored = record.Restore(new StopwatchTestingClock(), new AlwaysOnlineMonitor()))
                        {
                            restored.BeginSubmit(new ExamTaskGrade[0]);
                            for (int i = 1; i < 7; i++) restored.RecordUnvisitedResults(i, new ExamTaskGrade[0]);
                            restored.Complete(_ => { }); lab.Store.Save(record.Prepared, restored, null); lab.Store.ArchiveCompleted();
                        }
                        finalized = true;
                    });
                Assert.IsTrue(finalized); Assert.IsFalse(File.Exists(owned));
            }
        }
        [TestMethod]
        public void RollbackAuditSurvivesSubsequentStateWrites()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                var now = DateTime.UtcNow; session.Start(); lab.Store.Save(lab.Prepared, session, null);
                lab.Store.RecordRecoveryDecision(TestingRecoveryDecision.Evaluate(TestingSessionStatus.Running, TimeSpan.Zero, now, now.AddMinutes(-3), true));
                lab.Store.Save(lab.Prepared, session, null);
                Assert.IsTrue((bool)JObject.Parse(File.ReadAllText(lab.Store.StatePath))["clockRollbackDetected"]);
            }
        }

        [TestMethod]
        public void FailedVerifiedStopDoesNotFinalizeOrRecover()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                session.Start(); lab.Store.Save(lab.Prepared, session, null);
                lab.Store.RecordOwnedWord(lab.Prepared, new OwnedWordProcessIdentity(123, 100000, @"C:\Office\WINWORD.EXE"));
                var journal = new SessionRecoveryJournal(Path.Combine(lab.Folder, "journal.json"), _ => { });
                string file = Path.Combine(lab.Folder, "work.docx"); File.WriteAllText(file, "preserve"); journal.Track(file, "working", lab.Prepared.SessionId);
                new TestingStartupRecovery(lab.Store, journal).Inspect(lab.Catalog, lab.Packages, DateTime.UtcNow,
                    (id, machine) => true, id => true, (id, machine) => false,
                    record => { Assert.Fail("Stop was not verified."); return TestingResumeChoice.Submit; }, record => Assert.Fail("Must remain pending."));
                journal.Recover(); Assert.IsTrue(File.Exists(file)); Assert.IsTrue(File.Exists(lab.Store.StatePath));
            }
        }
        [TestMethod]
        public void FailedFinalizationCannotReleaseRecoveryClaims()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                session.Start(); lab.Store.Save(lab.Prepared, session, null);
                var journal = new SessionRecoveryJournal(Path.Combine(lab.Folder, "journal.json"), _ => { });
                string file = Path.Combine(lab.Folder, "work.docx"); File.WriteAllText(file, "preserve"); journal.Track(file, "working", lab.Prepared.SessionId);
                Assert.ThrowsException<InvalidOperationException>(() => new TestingStartupRecovery(lab.Store, journal).Inspect(lab.Catalog, lab.Packages, DateTime.UtcNow,
                    (id, machine) => false, id => true, (id, machine) => true, record => TestingResumeChoice.Submit, record => { }));
                journal.Recover(); Assert.IsTrue(File.Exists(file));
            }
        }

        [TestMethod]
        public void HydratedOutputSequenceCannotMakeFreshObservationsOlderThanTheBaseline()
        {
            var baseline = new OutputFileState(@"C:\isolated\Memo.txt", false, 0, null, DateTime.MinValue, null, DateTime.UtcNow, 1000000000);
            var current = new OutputFileState(baseline.Path, false, 0, null, DateTime.MinValue, null, DateTime.UtcNow);
            Assert.IsTrue(current.ObservationSequence > baseline.ObservationSequence);
            var older = new OutputFileState(baseline.Path, false, 0, null, DateTime.MinValue, null, DateTime.UtcNow, 1);
            var later = new OutputFileState(baseline.Path, false, 0, null, DateTime.MinValue, null, DateTime.UtcNow);
            Assert.IsTrue(later.ObservationSequence > current.ObservationSequence); Assert.AreEqual(1L, older.ObservationSequence);
        }

        [TestMethod]
        public void ResumeRecomputesDowntimeAtAttachmentInsteadOfReusingInspectTime()
        {
            using (var lab = new Lab())
            using (var session = lab.Session())
            {
                session.Start(); lab.Clock.Advance(60); lab.Store.Save(lab.Prepared, session, null);
                var data = JObject.Parse(File.ReadAllText(lab.Store.StatePath)); data["lastHeartbeatUtc"] = lab.Clock.UtcNow; File.WriteAllText(lab.Store.StatePath, data.ToString());
                var record = lab.Store.Load(lab.Catalog, lab.Packages);
                Assert.AreEqual(TimeSpan.FromSeconds(60), record.Decide(lab.Clock.UtcNow).Elapsed);
                lab.Clock.Advance(300);
                using (var restored = record.Restore(lab.Clock, new AlwaysOnlineMonitor())) Assert.AreEqual(TimeSpan.FromSeconds(360), restored.Elapsed);
            }
        }

        private sealed class FakeIdentitySource : IWordProcessIdentity
        {
            public OwnedWordProcessIdentity OwnedProcessIdentity { get; private set; }
            public event Action<OwnedWordProcessIdentity> OwnedProcessStarted;
            internal void Start(OwnedWordProcessIdentity identity) { OwnedProcessIdentity = identity; OwnedProcessStarted?.Invoke(identity); }
        }

        private sealed class Lab : IDisposable
        {
            private readonly TestWorkspace folder = new TestWorkspace();
            internal string Folder => folder.Folder;
            internal readonly string Catalog = Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json");
            internal readonly List<ProjectPackage> Packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en").ToList();
            internal readonly TestingSessionTests.FakeClock Clock = new TestingSessionTests.FakeClock();
            internal readonly TestingSessionStateStore Store;
            internal readonly PreparedTestingSession Prepared;
            internal Lab() { Store = new TestingSessionStateStore(Path.Combine(Folder, "testing-session.json")); Prepared = Store.Prepare(Catalog, Packages, "en"); }
            internal TestingSession Session() => new TestingSession(Prepared.SessionId, Prepared.Definition, Clock, new AlwaysOnlineMonitor());
            public void Dispose() { folder.Dispose(); }
        }
    }
}
