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
        private sealed class Runtime : ITestingProjectRuntime
        {
            internal int Saves, FromDisk, Restarts, Entries; internal bool Enabled;
            private readonly ExamDefinition definition; private string current;
            internal Runtime(ExamDefinition definition) { this.definition = definition; }
            public void Enter(string id, bool resume) { current = id; Entries++; }
            public void AutosaveCurrent() { Saves++; }
            public void Observe() { }
            public void SetInputEnabled(bool enabled) { Enabled = enabled; }
            private IReadOnlyList<ExamTaskGrade> Grades(string id) => definition.Projects.Single(p => p.ProjectId == id).TaskIds
                .Select(t => new ExamTaskGrade(id, t, new TaskGradeResult(TaskGradeOutcome.Pass, "", "", t))).ToList();
            public void LeaveAndGrade(Action<IReadOnlyList<ExamTaskGrade>> persist) { persist(Grades(current)); }
            public IReadOnlyList<ExamTaskGrade> GradeSaved(string id) { FromDisk++; return Grades(id); }
            public void Restart(string id) { Restarts++; }
            public void Close() { }
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
