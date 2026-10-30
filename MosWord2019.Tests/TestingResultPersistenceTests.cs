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
    public sealed class TestingResultPersistenceTests
    {
        [TestMethod]
        public void CompletedResultHasSeedAuditTaskWeightsTimesAndOfflineHistoryWithoutDiagnostics()
        {
            using (var lab = new TestWorkspace())
            {
                var store = new TestingSessionStateStore(Path.Combine(lab.Folder, "testing-session.json"));
                var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "vi"), "vi");
                var clock = new Clock(); var network = new Network();
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition, clock, network))
                {
                    session.Start(); clock.Advance(10); network.Set(false); clock.Advance(12); network.Set(true); clock.Advance(3);
                    Func<int, IEnumerable<ExamTaskGrade>> grades = i => prepared.Definition.Projects[i].TaskIds.Select((t, j) => new ExamTaskGrade(prepared.Definition.Projects[i].ProjectId, t,
                        new TaskGradeResult(j == 0 ? TaskGradeOutcome.Error : TaskGradeOutcome.Pass, "PRIVATE_DIAGNOSTIC", "", t)));
                    session.BeginSubmit(grades(0)); for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, grades(i));
                    session.Complete(_ => { }); store.Save(prepared, session, clock.UtcNow);
                    var result = JObject.Parse(File.ReadAllText(store.ArchiveCompleted()));
                    Assert.AreEqual(true, (bool?)result["pendingUpload"]); Assert.AreEqual(prepared.Definition.Seed, (long)result["seed"]);
                    Assert.AreEqual(15, ((JObject)result["contentHashes"]).Count); Assert.AreEqual(prepared.Definition.N, ((JArray)result["tasks"]).Count);
                    Assert.AreEqual(1000, result["tasks"].Sum(t => (int)t["points"])); Assert.AreEqual(session.Result.Total, (int)result["total"]);
                    Assert.AreEqual(7, result["tasks"].Count(t => (string)t["outcome"] == "Error")); Assert.IsFalse(result.ToString().Contains("PRIVATE_DIAGNOSTIC"));
                    Assert.AreEqual(1, ((JArray)result["offlinePeriods"]).Count); Assert.AreEqual(TimeSpan.FromSeconds(12).Ticks, (long)result["offlinePeriods"][0]["DurationTicks"]);
                    Assert.AreEqual(clock.UtcNow, (DateTime)result["completedUtc"]); Assert.AreEqual(TimeSpan.FromSeconds(13).Ticks, (long)result["elapsedTicks"]);
                    Assert.IsFalse(File.Exists(store.StatePath));
                }
            }
        }
        [DataTestMethod]
        [DataRow("incomplete")]
        [DataRow("score")]
        [DataRow("conflict")]
        [DataRow("directory")]
        public void InvalidOrBlockedResultNeverDiscardsRecoveryState(string failure)
        {
            using (var lab = new TestWorkspace())
            {
                var store = new TestingSessionStateStore(Path.Combine(lab.Folder, "testing-session.json"));
                var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"), "en");
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition, new Clock(), new Network()))
                {
                    session.Start();
                    if (failure != "incomplete")
                    {
                        session.BeginSubmit(new ExamTaskGrade[0]); for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, new ExamTaskGrade[0]);
                        session.Complete(_ => { });
                    }
                    store.Save(prepared, session, null);
                    string target = Path.Combine(lab.Folder, "Results", prepared.SessionId + ".json");
                    if (failure == "score") { var data = JObject.Parse(File.ReadAllText(store.StatePath)); data["result"]["Total"] = 1000; File.WriteAllText(store.StatePath, data.ToString()); }
                    if (failure == "conflict") { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllText(target, "{}"); }
                    if (failure == "directory") File.WriteAllText(Path.Combine(lab.Folder, "Results"), "personal");
                    string before = File.ReadAllText(store.StatePath);
                    if (failure == "incomplete") Assert.ThrowsException<InvalidOperationException>(() => store.ArchiveCompleted());
                    else if (failure == "score") Assert.ThrowsException<InvalidDataException>(() => store.ArchiveCompleted());
                    else Assert.ThrowsException<IOException>(() => store.ArchiveCompleted());
                    Assert.AreEqual(before, File.ReadAllText(store.StatePath));
                    if (failure == "conflict") Assert.AreEqual("{}", File.ReadAllText(target));
                }
            }
        }
        [TestMethod]
        public void PendingResultIsIdempotentAndSurvivesLockedRecoveryStateUntilRetry()
        {
            using (var lab = new TestWorkspace())
            {
                var store = new TestingSessionStateStore(Path.Combine(lab.Folder, "testing-session.json"));
                var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"), "en");
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition, new Clock(), new Network()))
                {
                    session.Start(); session.BeginSubmit(new ExamTaskGrade[0]); for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, new ExamTaskGrade[0]);
                    session.Complete(_ => { }); store.Save(prepared, session, null);
                    string target = store.PersistCompletedResult(), before = File.ReadAllText(target);
                    store.Save(prepared, session, DateTime.UtcNow); // A post-completion cleanup/save observation must not invalidate a durable result.
                    Assert.AreEqual(target, store.PersistCompletedResult()); Assert.AreEqual(before, File.ReadAllText(target));
                    using (var held = new FileStream(store.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        Assert.ThrowsException<IOException>(() => store.ArchiveCompleted());
                    Assert.IsTrue(File.Exists(store.StatePath)); Assert.AreEqual(before, File.ReadAllText(target));
                    Assert.AreEqual(target, store.ArchiveCompleted()); Assert.IsFalse(File.Exists(store.StatePath));
                    Assert.AreEqual(prepared.Definition.N, JObject.Parse(before)["tasks"].Count(t => (string)t["outcome"] == "Error"));
                }
            }
        }
        [TestMethod]
        public void OfflineHistoryAndStartTimeSurviveResumeIncludingCrashDowntime()
        {
            using (var lab = new TestWorkspace())
            {
                var store = new TestingSessionStateStore(Path.Combine(lab.Folder, "testing-session.json"));
                string catalog = Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"); var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en");
                var prepared = store.Prepare(catalog, packages, "en"); var clock = new Clock(); var network = new Network(); DateTime start = clock.UtcNow;
                using (var session = new TestingSession(prepared.SessionId, prepared.Definition, clock, network))
                {
                    session.Start(); clock.Advance(10); network.Set(false); clock.Advance(12); store.Save(prepared, session, clock.UtcNow);
                }
                var data = JObject.Parse(File.ReadAllText(store.StatePath)); data["lastHeartbeatUtc"] = clock.UtcNow; File.WriteAllText(store.StatePath, data.ToString()); clock.Advance(5);
                using (var restored = store.Load(catalog, packages).Restore(clock, new Network()))
                {
                    var snapshot = restored.CaptureState(); Assert.AreEqual(start, snapshot.StartedUtc);
                    Assert.AreEqual(1, snapshot.OfflinePeriods.Count); Assert.AreEqual(TimeSpan.FromSeconds(17).Ticks, snapshot.OfflinePeriods[0].DurationTicks);
                    Assert.AreEqual(clock.UtcNow, snapshot.OfflinePeriods[0].EndedUtc); Assert.AreEqual(TimeSpan.FromSeconds(15), restored.Elapsed);
                }
            }
        }
        private sealed class Clock : ITestingClock
        {
            public TimeSpan Monotonic { get; private set; }
            public DateTime UtcNow => new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc) + Monotonic;
            internal void Advance(int seconds) { Monotonic += TimeSpan.FromSeconds(seconds); }
        }
        private sealed class Network : IConnectivityMonitor
        {
            public bool IsOnline { get; private set; } = true; public event Action<bool> ConnectivityChanged;
            internal void Set(bool online) { IsOnline = online; ConnectivityChanged?.Invoke(online); }
        }
    }
}
