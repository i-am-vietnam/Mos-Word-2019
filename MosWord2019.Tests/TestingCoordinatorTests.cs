using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TestingCoordinatorTests
    {
        [TestMethod]
        public void PreflightFailureDoesNotStartTimerAndRetryUsesSameSeed()
        {
            using (var lab = new Lab())
            {
                lab.FailPreflight = true; Assert.ThrowsException<IOException>(() => lab.Engine.Start());
                Assert.AreEqual(TestingSessionStatus.Ready, lab.Session.Status);
                lab.FailPreflight = false; lab.Engine.Start(); Assert.AreEqual(TestingSessionStatus.Running, lab.Session.Status);
                Assert.AreEqual(lab.Prepared.Definition.Seed, lab.Session.Definition.Seed);
            }
        }
        [TestMethod]
        public void AutosaveBoundaryRestartAndOneWayNextPreserveClockAndGrades()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Clock.Advance(59); lab.Engine.Tick(); Assert.AreEqual(0, lab.Runtime.Saves);
                lab.Clock.Advance(1); lab.Engine.Tick(); Assert.AreEqual(1, lab.Runtime.Saves);
                lab.Engine.Restart(); Assert.AreEqual(0, lab.Session.CurrentIndex); Assert.AreEqual(60, lab.Session.Elapsed.TotalSeconds);
                lab.Engine.Next(); Assert.AreEqual(1, lab.Session.CurrentIndex); Assert.AreEqual(TestingProjectState.Locked, lab.Session.ProjectStates[0]);
                Assert.AreEqual(1, lab.Runtime.Restarts); Assert.IsTrue(lab.Session.GetLockedResults().Count >= 4);
            }
        }
        [DataTestMethod]
        [DataRow("submit")]
        [DataRow("timeout")]
        [DataRow("offline")]
        public void FinalizeGradesUnvisitedFromSavedSourcesAndStoresResultBeforeArchive(string reason)
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start();
                if (reason == "submit") { var now = DateTime.UtcNow; lab.Store.RecordRecoveryDecision(MosWord2019.Core.Services.TestingRecoveryDecision.Evaluate(TestingSessionStatus.Running, TimeSpan.Zero, now, now.AddMinutes(-3), true)); lab.Engine.Submit(); }
                if (reason == "timeout") { lab.Clock.Advance(3000); lab.Engine.Tick(); }
                if (reason == "offline") { lab.Network.Set(false); lab.Engine.Tick(); Assert.IsFalse(lab.Runtime.Enabled); lab.Clock.Advance(1800); lab.Engine.Tick(); }
                Assert.AreEqual(TestingSessionStatus.Completed, lab.Session.Status);
                Assert.AreEqual(6, lab.Runtime.FromDisk); Assert.AreEqual(1000, lab.Session.Result.Total);
                Assert.IsFalse(File.Exists(lab.Store.StatePath)); Assert.AreEqual(1, Directory.GetFiles(Path.Combine(lab.Folder, "Results")).Length);
                Assert.IsFalse(lab.Runtime.Enabled);
                if (reason == "submit") Assert.IsTrue((bool)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(lab.Folder, "Results")).Single()))["clockRollbackDetected"]);
            }
        }
        [TestMethod]
        public void OfflinePausesAndReconnectionRestoresInput()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Clock.Advance(10); lab.Network.Set(false); lab.Engine.Tick(); lab.Clock.Advance(100); lab.Engine.Tick();
                Assert.AreEqual(10, lab.Session.Elapsed.TotalSeconds); Assert.IsFalse(lab.Runtime.Enabled);
                lab.Network.Set(true); lab.Engine.Tick(); lab.Clock.Advance(1); Assert.IsTrue(lab.Runtime.Enabled); Assert.AreEqual(11, lab.Session.Elapsed.TotalSeconds);
            }
        }
        [TestMethod]
        public void AllSevenProjectsCanBeCompletedInOrder()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); for (int i = 0; i < 6; i++) lab.Engine.Next();
                Assert.ThrowsException<InvalidOperationException>(() => lab.Engine.Next()); lab.Engine.Submit();
                Assert.AreEqual(0, lab.Runtime.FromDisk); Assert.AreEqual(1000, lab.Session.Result.Total);
            }
        }
        [TestMethod]
        public void ExpiredResumeFinalizesAllSavedProjectsWithoutOpeningWord()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Store.Save(lab.Prepared, lab.Session, null);
                var data = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(lab.Store.StatePath));
                data["lastHeartbeatUtc"] = lab.Clock.UtcNow.AddMinutes(-16); File.WriteAllText(lab.Store.StatePath, data.ToString());
                var record = lab.Store.Load(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"));
                using (var restored = record.Restore(lab.Clock, lab.Network))
                {
                    var runtime = new Runtime(record.Prepared.Definition);
                    using (var coordinator = new TestingSessionCoordinator(record.Prepared, restored, lab.Store, lab.Clock, runtime, () => { }, _ => { }))
                    {
                        coordinator.Resume(); Assert.AreEqual(7, runtime.FromDisk); Assert.AreEqual(0, runtime.Entries);
                        Assert.AreEqual(TestingSessionStatus.Completed, restored.Status);
                    }
                }
            }
        }

        [TestMethod]
        public void ObservationBackoffCannotDelayTimeoutLockAndSubmission()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Clock.Advance(3000); lab.Engine.Tick(false);
                Assert.AreEqual(TestingSessionStatus.Completed, lab.Session.Status); Assert.IsFalse(lab.Runtime.Enabled);
            }
        }

        [DataTestMethod]
        [DataRow(false, 1)]
        [DataRow(false, 3)]
        [DataRow(true, 1)]
        [DataRow(true, 3)]
        public void BusyNextOrSubmitCanBeRetriedWithoutFaulting(bool submit, int attempts)
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Runtime.BusyRemaining = attempts;
                for (int i = 0; i < attempts; i++)
                {
                    Assert.ThrowsException<MosWord2019.Core.Services.WordBusyException>(() => { if (submit) lab.Engine.Submit(); else lab.Engine.Next(); });
                    Assert.AreEqual(TestingSessionStatus.Running, lab.Session.Status); Assert.AreEqual(0, lab.Session.CurrentIndex);
                }
                if (submit) { lab.Engine.Submit(); Assert.AreEqual(TestingSessionStatus.Completed, lab.Session.Status); }
                else { lab.Engine.Next(); Assert.AreEqual(1, lab.Session.CurrentIndex); }
            }
        }
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(4)]
        [DataRow(100000)]
        public void TimeoutRetriesBusyThenForcesSavedFinalizationAfterThirtySeconds(int attempts)
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Runtime.BusyRemaining = attempts; lab.Clock.Advance(3000);
                for (int i = 0; i < 5 && lab.Session.Status != TestingSessionStatus.Completed; i++)
                { lab.Engine.Tick(); lab.Clock.Advance(10); }
                Assert.AreEqual(TestingSessionStatus.Completed, lab.Session.Status);
                Assert.AreEqual(attempts >= 4 ? 1 : 0, lab.Runtime.Forced);
                Assert.IsTrue(Directory.GetFiles(Path.Combine(lab.Folder, "Results")).Length == 1);
            }
        }

        [TestMethod]
        public void BusyOpeningNextProjectRetriesEntryWithoutSkippingAnotherProject()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Runtime.EntryBusyRemaining = 1;
                Assert.ThrowsException<MosWord2019.Core.Services.WordBusyException>(() => lab.Engine.Next());
                Assert.AreEqual(1, lab.Session.CurrentIndex); lab.Engine.Tick();
                Assert.AreEqual(1, lab.Session.CurrentIndex); Assert.AreEqual(2, lab.Runtime.Entries);
                lab.Engine.Next(); Assert.AreEqual(2, lab.Session.CurrentIndex);
            }
        }
        [TestMethod]
        public void BusyResumeEntryRetainsResumeIntent()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Store.Save(lab.Prepared, lab.Session, null);
                var record = lab.Store.Load(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"));
                using (var restored = record.Restore(lab.Clock, lab.Network))
                {
                    var runtime = new Runtime(record.Prepared.Definition) { EntryBusyRemaining = 1 };
                    using (var engine = new TestingSessionCoordinator(record.Prepared, restored, lab.Store, lab.Clock, runtime, () => { }, _ => { }))
                    {
                        Assert.ThrowsException<MosWord2019.Core.Services.WordBusyException>(() => engine.Resume());
                        engine.Tick(); Assert.IsTrue(runtime.LastResume); Assert.AreEqual(0, restored.CurrentIndex);
                    }
                }
            }
        }

        [TestMethod]
        public void RestoredHostTickCannotResetWorkspaceBeforeResumeAttachment()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Store.Save(lab.Prepared, lab.Session, null);
                var record = lab.Store.Load(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"));
                using (var restored = record.Restore(lab.Clock, lab.Network))
                {
                    var runtime = new Runtime(record.Prepared.Definition);
                    using (var engine = new TestingSessionCoordinator(record.Prepared, restored, lab.Store, lab.Clock, runtime, () => { }, _ => { }))
                    {
                        engine.Tick(); Assert.AreEqual(0, runtime.Entries);
                        lab.Clock.Advance(30); engine.Resume();
                        Assert.IsTrue(runtime.LastResume); Assert.AreEqual(1, runtime.Entries);
                        Assert.AreEqual(TimeSpan.FromSeconds(30), restored.Elapsed);
                    }
                }
            }
        }

        [TestMethod]
        public void FailedForceCleanupStillStoresCompletedResultWithoutLiveWordCalls()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Runtime.BusyRemaining = int.MaxValue; lab.Runtime.ForceFails = true;
                lab.Clock.Advance(3000); lab.Engine.Tick(); lab.Clock.Advance(30); lab.Engine.Tick();
                Assert.AreEqual(TestingSessionStatus.Completed, lab.Session.Status);
                Assert.IsTrue(lab.Engine.CleanupPending); Assert.IsFalse(lab.Engine.NeedsRecovery);
                Assert.IsTrue(File.Exists(Path.Combine(lab.Folder, "Results", lab.Prepared.SessionId + ".json")));
                Assert.IsNotNull(Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(lab.Store.StatePath))["result"]);
                Assert.AreEqual(7, lab.Runtime.FromDisk);
            }
        }

        [DataTestMethod]
        [DataRow(unchecked((int)0x80010001))]
        [DataRow(unchecked((int)0x8001010A))]
        public void RawRpcBusyIsRetryable(int code)
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Runtime.BusyCode = code; lab.Runtime.BusyRemaining = 1;
                Assert.ThrowsException<System.Runtime.InteropServices.COMException>(() => lab.Engine.Next());
                Assert.IsFalse(lab.Engine.NeedsRecovery); lab.Engine.Next(); Assert.AreEqual(1, lab.Session.CurrentIndex);
            }
        }
        [TestMethod]
        public void OfflineLimitAlsoForcesSavedResultAfterPersistentBusy()
        {
            using (var lab = new Lab())
            {
                lab.Engine.Start(); lab.Network.Set(false); lab.Runtime.BusyRemaining = int.MaxValue;
                lab.Clock.Advance(1800); lab.Engine.Tick(); lab.Clock.Advance(30); lab.Engine.Tick();
                Assert.AreEqual(TestingSessionStatus.Completed, lab.Session.Status); Assert.AreEqual(1, lab.Runtime.Forced);
            }
        }
        private sealed class Clock : ITestingClock
        {
            public TimeSpan Monotonic { get; private set; }
            public DateTime UtcNow => new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc) + Monotonic;
            internal void Advance(int s) { Monotonic += TimeSpan.FromSeconds(s); }
        }
        private sealed class Network : IConnectivityMonitor
        {
            public bool IsOnline { get; private set; } = true;
            public event Action<bool> ConnectivityChanged;
            internal void Set(bool value) { IsOnline = value; ConnectivityChanged?.Invoke(value); }
        }
        private sealed class Runtime : ITestingProjectRuntime, ITestingForceRecovery
        {
            internal int Saves, FromDisk, Restarts, Entries, BusyRemaining, Forced, EntryBusyRemaining; internal bool LastResume; internal bool Enabled, ForceFails; internal int BusyCode;
            private readonly ExamDefinition definition; private string current;
            internal Runtime(ExamDefinition definition) { this.definition = definition; }
            public void Enter(string id, bool resume) { LastResume = resume; if (EntryBusyRemaining-- > 0) throw new MosWord2019.Core.Services.WordBusyException(new Exception("entry dialog")); current = id; Entries++; }
            public void AutosaveCurrent() { Saves++; }
            public void Observe() { }
            public void SetInputEnabled(bool enabled) { if (ForceFails && Forced > 0) throw new IOException("Word unavailable"); Enabled = enabled; }
            private IReadOnlyList<ExamTaskGrade> Grades(string id) => definition.Projects.Single(p => p.ProjectId == id).TaskIds
                .Select(t => new ExamTaskGrade(id, t, new TaskGradeResult(TaskGradeOutcome.Pass, "", "", t))).ToList();
            public void LeaveAndGrade(Action<IReadOnlyList<ExamTaskGrade>> persist) { if (BusyRemaining-- > 0) { if (BusyCode != 0) throw new System.Runtime.InteropServices.COMException("busy", BusyCode); throw new MosWord2019.Core.Services.WordBusyException(new Exception("fake dialog")); } persist(Grades(current)); }
            public void ForceCloseOwnedWord() { Forced++; if (ForceFails) throw new IOException("termination denied"); BusyRemaining = 0; }
            public IReadOnlyList<ExamTaskGrade> GradeSaved(string id) { FromDisk++; return Grades(id); }
            public void Restart(string id) { Restarts++; }
            public void Close() { if (ForceFails) throw new IOException("cleanup locked"); }
            public void Dispose() { }
        }
        private sealed class Lab : IDisposable
        {
            private readonly TestWorkspace folder = new TestWorkspace();
            internal string Folder => folder.Folder;
            internal readonly Clock Clock = new Clock(); internal readonly Network Network = new Network();
            internal readonly PreparedTestingSession Prepared; internal readonly TestingSession Session;
            internal readonly TestingSessionStateStore Store; internal readonly Runtime Runtime; internal readonly TestingSessionCoordinator Engine;
            internal bool FailPreflight;
            internal Lab()
            {
                Store = new TestingSessionStateStore(Path.Combine(Folder, "testing-session.json"));
                Prepared = Store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"), "en");
                Session = new TestingSession(Prepared.SessionId, Prepared.Definition, Clock, Network); Runtime = new Runtime(Prepared.Definition);
                Engine = new TestingSessionCoordinator(Prepared, Session, Store, Clock, Runtime, () => { if (FailPreflight) throw new IOException("conflicting asset"); }, _ => { });
            }
            public void Dispose() { Engine.Dispose(); Session.Dispose(); folder.Dispose(); }
        }
    }
}
