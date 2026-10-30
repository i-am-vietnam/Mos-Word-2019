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
    public sealed class TestingSessionTests
    {
        [TestMethod]
        public void ClockStartsOnlyAfterStartAndRestartKeepsTimeAndOnlyCurrentProject()
        {
            var clock = new FakeClock();
            using (var session = Create(clock))
            {
                clock.Advance(100);
                Assert.AreEqual(TimeSpan.Zero, session.Elapsed);
                session.Start(); clock.Advance(20);
                session.NextProject(Grades(session, 0));
                session.RestartCurrent();
                Assert.AreEqual(1, session.CurrentIndex);
                Assert.AreEqual(TimeSpan.FromSeconds(20), session.Elapsed);
                Assert.AreEqual(TestingProjectState.Locked, session.ProjectStates[0]);
                Assert.AreEqual(TestingProjectState.Active, session.ProjectStates[1]);
                Assert.ThrowsException<InvalidOperationException>(() => session.Start());
                for (int i = 1; i < 6; i++) session.NextProject(Grades(session, i));
                Assert.ThrowsException<InvalidOperationException>(() => session.NextProject(Grades(session, 6)));
            }
        }

        [TestMethod]
        public void TimeoutRequestsLockedFinalizationWithoutConfirmation()
        {
            var clock = new FakeClock();
            using (var session = Create(clock))
            {
                session.Start(); clock.Advance(3000); session.Tick();
                Assert.IsTrue(session.InputLocked);
                Assert.AreEqual(TestingFinalizeReason.Timeout, session.FinalizeReason);
                Assert.AreEqual(TimeSpan.Zero, session.Remaining);
                Assert.ThrowsException<InvalidOperationException>(() => session.RestartCurrent());
                session.BeginSubmit(Grades(session, 0));
                for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, Grades(session, i));
                Assert.AreEqual(1000, session.Complete(_ => Assert.Fail()).Total);
                Assert.ThrowsException<InvalidOperationException>(() => session.NextProject(Grades(session, 0)));
            }
        }

        [DataTestMethod]
        [DataRow(1799, false)]
        [DataRow(1800, true)]
        public void OfflinePausesAndHasExactThirtyMinuteLimit(int wait, bool finalize)
        {
            var clock = new FakeClock(); var network = new FakeNetwork();
            using (var session = Create(clock, network))
            {
                session.Start(); clock.Advance(30); network.Set(false);
                Assert.IsTrue(session.OverlayRequested); Assert.IsTrue(session.InputLocked);
                clock.Advance(wait); session.Tick();
                Assert.AreEqual(TimeSpan.FromSeconds(30), session.Elapsed);
                if (finalize) Assert.AreEqual(TestingFinalizeReason.OfflineLimit, session.FinalizeReason);
                else
                {
                    network.Set(true); clock.Advance(10);
                    Assert.IsFalse(session.OverlayRequested);
                    Assert.AreEqual(TimeSpan.FromSeconds(40), session.Elapsed);
                }
            }
        }

        [TestMethod]
        public void BackwardUtcAndRepeatedOfflineEventsCannotExtendOfflineDeadline()
        {
            var clock = new FakeClock(); var network = new FakeNetwork();
            using (var session = Create(clock, network))
            {
                session.Start(); clock.Advance(40); clock.Utc = clock.Utc.AddDays(-1);
                Assert.AreEqual(TimeSpan.FromSeconds(40), session.Elapsed);
                network.Set(false); clock.Advance(1700); network.Set(false); clock.Advance(100);
                network.Set(true);
                Assert.AreEqual(TestingFinalizeReason.OfflineLimit, session.FinalizeReason);
                Assert.IsTrue(session.InputLocked);
            }
        }

        [TestMethod]
        public void EarlySubmitAcceptsOfflineAndCannotCompleteBeforeAllProjectsAreRecorded()
        {
            var clock = new FakeClock(); var network = new FakeNetwork();
            using (var session = Create(clock, network))
            {
                session.Start(); network.Set(false); session.BeginSubmit(Grades(session, 0));
                Assert.AreEqual(TestingFinalizeReason.Submitted, session.FinalizeReason);
                Assert.ThrowsException<InvalidOperationException>(() => session.Complete(_ => { }));
                Assert.ThrowsException<InvalidOperationException>(() => session.RecordUnvisitedResults(0, Grades(session, 0)));
                for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, Grades(session, i));
                Assert.AreEqual(1000, session.Complete(_ => Assert.Fail()).Total);
            }
        }

        [TestMethod]
        public void WrongProjectResultsAndUseAfterDisposeAreRejected()
        {
            var session = Create(new FakeClock()); session.Start();
            Assert.ThrowsException<InvalidDataException>(() => session.NextProject(Grades(session, 1)));
            Assert.AreEqual(0, session.CurrentIndex);
            session.Dispose();
            Assert.ThrowsException<ObjectDisposedException>(() => session.Tick());
        }

        [TestMethod]
        public void WholeMissingProjectIsLoggedAndGetsNoPoints()
        {
            var definition = Definition(); var log = new List<string>();
            var grades = definition.Projects.Skip(1).SelectMany(p => p.TaskIds.Select(id => new ExamTaskGrade(p.ProjectId, id,
                new TaskGradeResult(TaskGradeOutcome.Pass, "", "test", id))));
            var result = new ExamScorer(log.Add).Score(definition, grades);
            Assert.AreEqual(definition.Projects[0].TaskIds.Count, log.Count);
            Assert.IsTrue(log.All(s => s.Contains("missing grade") && s.Contains(definition.Projects[0].ProjectId)));
            Assert.AreEqual(0, result.PerProject[0].Points);
            Assert.AreEqual(log.Count, result.WrongTasks.Count);
        }

        [TestMethod]
        public void SnapshotIsConsistentImmutableAndChecksDeadlineAndDisposeFreezesTime()
        {
            var clock = new FakeClock(); var network = new FakeNetwork();
            var session = Create(clock, network); session.Start();
            clock.Advance(10); var before = session.CaptureState();
            session.NextProject(Grades(session, 0)); clock.Advance(10);
            Assert.AreEqual(0, before.CurrentIndex); Assert.AreEqual(0, before.LockedResults.Count);
            Assert.AreEqual(TestingProjectState.Active, before.ProjectStates[0]);
            session.Dispose(); clock.Advance(100); network.Set(false);
            Assert.AreEqual(TimeSpan.FromSeconds(20), session.Elapsed);
            Assert.ThrowsException<ObjectDisposedException>(() => session.CaptureState());
            using (var timed = Create(clock))
            {
                timed.Start(); clock.Advance(3000);
                Assert.AreEqual(TestingFinalizeReason.Timeout, timed.CaptureState().FinalizeReason);
            }
        }

        internal static ExamDefinition Definition()
        {
            var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en");
            return ExamComposer.Compose(new TestingCatalogLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages), packages, 42);
        }
        private static TestingSession Create(FakeClock clock, FakeNetwork network = null) =>
            new TestingSession(Guid.NewGuid().ToString("N"), Definition(), clock, network ?? new FakeNetwork());
        private static List<ExamTaskGrade> Grades(TestingSession session, int index) =>
            session.Definition.Projects[index].TaskIds.Select(id => new ExamTaskGrade(session.Definition.Projects[index].ProjectId, id,
                new TaskGradeResult(TaskGradeOutcome.Pass, "", "test", id))).ToList();
        internal sealed class FakeClock : ITestingClock
        {
            public TimeSpan Monotonic { get; private set; }
            public DateTime Utc = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
            public DateTime UtcNow => Utc;
            public void Advance(int seconds) { Monotonic += TimeSpan.FromSeconds(seconds); Utc = Utc.AddSeconds(seconds); }
        }
        private sealed class FakeNetwork : IConnectivityMonitor
        {
            public bool IsOnline { get; private set; } = true;
            public event Action<bool> ConnectivityChanged;
            internal void Set(bool online) { IsOnline = online; ConnectivityChanged?.Invoke(online); }
        }
    }
}
