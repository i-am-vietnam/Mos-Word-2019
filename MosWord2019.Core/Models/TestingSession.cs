using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Services;

namespace MosWord2019.Core.Models
{
    public enum TestingProjectState { NotStarted, Active, Locked }
    public enum TestingSessionStatus { Ready, Running, Offline, Submitting, Completed }
    public enum TestingFinalizeReason { None, Submitted, Timeout, OfflineLimit, RecoveryExpired }

    /// <summary>Thread-safe pure state. The owner must perform Word/save/grade operations before committing transitions.</summary>
    public sealed class TestingSession : IDisposable
    {
        public static readonly TimeSpan Duration = TimeSpan.FromMinutes(50);
        public static readonly TimeSpan OfflineLimit = TimeSpan.FromMinutes(30);
        private readonly object gate = new object();
        private readonly ITestingClock clock;
        private readonly IConnectivityMonitor connectivity;
        private readonly TestingProjectState[] states = new TestingProjectState[7];
        private readonly List<ExamTaskGrade>[] grades = new List<ExamTaskGrade>[7];
        private TimeSpan elapsed, segmentStart, offlineStart;
        private TestingSessionStatus status;
        private TestingFinalizeReason reason;
        private int current;
        private bool disposed;
        private ExamResult result;
        private DateTime? startedUtc, completedUtc, offlinePeriodStart;
        private readonly List<TestingOfflinePeriod> offlinePeriods = new List<TestingOfflinePeriod>();
        private bool historyIncomplete;
        public string SessionId { get; }
        public ExamDefinition Definition { get; }
        public ExamResult Result { get { lock (gate) return result; } }
        public TestingSessionStatus Status { get { lock (gate) return status; } }
        public TestingFinalizeReason FinalizeReason { get { lock (gate) return reason; } }
        public int CurrentIndex { get { lock (gate) return current; } }
        public bool InputLocked { get { lock (gate) return status != TestingSessionStatus.Running; } }
        public bool OverlayRequested { get { lock (gate) return status == TestingSessionStatus.Offline; } }
        public IReadOnlyList<TestingProjectState> ProjectStates { get { lock (gate) return states.ToList().AsReadOnly(); } }
        public TimeSpan Elapsed { get { lock (gate) return ReadElapsed(); } }
        public TimeSpan Remaining => Duration - Elapsed;

        public TestingSession(string sessionId, ExamDefinition definition, ITestingClock clock, IConnectivityMonitor connectivity)
        {
            Guid id;
            if (!Guid.TryParseExact(sessionId, "N", out id)) throw new ArgumentException("A session GUID is required.", nameof(sessionId));
            SessionId = sessionId;
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
            connectivity.ConnectivityChanged += ConnectivityChanged;
        }

        public static TestingSession Restore(string sessionId, ExamDefinition definition, TestingSessionSnapshot snapshot,
            TestingRecoveryDecision decision, ITestingClock clock, IConnectivityMonitor connectivity)
        {
            if (snapshot == null || decision == null || snapshot.ProjectStates == null || snapshot.LockedResults == null ||
                snapshot.CurrentIndex < 0 || snapshot.CurrentIndex >= 7 || snapshot.ProjectStates.Count != 7 ||
                snapshot.ProjectStates.Any(p => !Enum.IsDefined(typeof(TestingProjectState), p)) || snapshot.LockedResults.Any(g => g == null) ||
                !new[] { TestingSessionStatus.Running, TestingSessionStatus.Offline, TestingSessionStatus.Submitting }.Contains(snapshot.Status) ||
                !Enum.IsDefined(typeof(TestingFinalizeReason), snapshot.FinalizeReason) || snapshot.OfflineElapsed < TimeSpan.Zero ||
                (decision.Action != TestingRecoveryAction.Resume && decision.Action != TestingRecoveryAction.Finalize))
                throw new InvalidDataException("Invalid Testing resume state.");
            var session = new TestingSession(sessionId, definition, clock, connectivity);
            try
            {
                session.current = snapshot.CurrentIndex;
                session.startedUtc = snapshot.StartedUtc;
                session.historyIncomplete = snapshot.HistoryIncomplete || !snapshot.StartedUtc.HasValue;
                var open = snapshot.OfflinePeriods.Where(p => !p.EndedUtc.HasValue).ToList();
                if (open.Count > 1 || open.Count == 1 && (snapshot.Status != TestingSessionStatus.Offline || snapshot.OfflinePeriods.Last() != open[0]))
                    throw new InvalidDataException("Invalid offline audit history.");
                session.offlinePeriods.AddRange(snapshot.OfflinePeriods.Where(p => p.EndedUtc.HasValue));
                session.offlinePeriodStart = open.Count == 1 ? (DateTime?)open[0].StartedUtc : null;
                for (int i = 0; i < 7; i++)
                {
                    var state = snapshot.ProjectStates[i];
                    if (i < session.current && state != TestingProjectState.Locked || i == session.current && (state == TestingProjectState.NotStarted ||
                        snapshot.Status != TestingSessionStatus.Submitting && state != TestingProjectState.Active) ||
                        i > session.current && state == TestingProjectState.Active || snapshot.Status != TestingSessionStatus.Submitting && i > session.current && state != TestingProjectState.NotStarted)
                        throw new InvalidDataException("Inconsistent Testing project states.");
                    if (state == TestingProjectState.Locked)
                        session.Lock(i, snapshot.LockedResults.Where(g => g.ProjectId == definition.Projects[i].ProjectId));
                    else session.states[i] = state;
                }
                if (snapshot.LockedResults.Any(g => g == null || !definition.Projects.Select((p, i) => new { p.ProjectId, State = session.states[i] })
                    .Any(p => p.ProjectId == g.ProjectId && p.State == TestingProjectState.Locked))) throw new InvalidDataException("Unbound locked grades.");
                session.elapsed = decision.Elapsed; session.segmentStart = clock.Monotonic;
                session.offlineStart = clock.Monotonic - snapshot.OfflineElapsed - decision.Downtime;
                session.reason = snapshot.FinalizeReason;
                session.status = decision.Action == TestingRecoveryAction.Finalize ? TestingSessionStatus.Submitting :
                    connectivity.IsOnline ? TestingSessionStatus.Running : TestingSessionStatus.Offline;
                if (decision.Action == TestingRecoveryAction.Finalize && session.reason == TestingFinalizeReason.None)
                    session.reason = decision.Remaining <= TimeSpan.Zero ? TestingFinalizeReason.Timeout : TestingFinalizeReason.RecoveryExpired;
                if (snapshot.Status != TestingSessionStatus.Offline) session.offlineStart = clock.Monotonic;
                if (snapshot.Status == TestingSessionStatus.Offline && snapshot.OfflineElapsed + decision.Downtime >= OfflineLimit)
                { session.status = TestingSessionStatus.Submitting; session.reason = TestingFinalizeReason.OfflineLimit; }
                if (snapshot.Status == TestingSessionStatus.Offline && session.status != TestingSessionStatus.Offline)
                    session.CloseOfflinePeriod();
                if (session.status == TestingSessionStatus.Offline && !session.offlinePeriodStart.HasValue)
                { session.offlinePeriodStart = clock.UtcNow; session.historyIncomplete |= snapshot.Status == TestingSessionStatus.Offline; }
                session.CheckDeadlines();
                return session;
            }
            catch { session.Dispose(); throw; }
        }

        public void Start()
        {
            lock (gate)
            {
                EnsureUsable();
                if (status != TestingSessionStatus.Ready) throw new InvalidOperationException("Session already started.");
                startedUtc = clock.UtcNow;
                segmentStart = offlineStart = clock.Monotonic;
                status = connectivity.IsOnline ? TestingSessionStatus.Running : TestingSessionStatus.Offline;
                if (!connectivity.IsOnline) offlinePeriodStart = clock.UtcNow;
                states[0] = TestingProjectState.Active;
            }
        }
        public void Tick() { lock (gate) { EnsureUsable(); CheckDeadlines(); } }
        private void CheckDeadlines()
        {
            if (status == TestingSessionStatus.Running && ReadElapsed() >= Duration)
            { elapsed = Duration; status = TestingSessionStatus.Submitting; reason = TestingFinalizeReason.Timeout; }
            if (status == TestingSessionStatus.Offline && clock.Monotonic - offlineStart >= OfflineLimit)
            { CloseOfflinePeriod(); status = TestingSessionStatus.Submitting; reason = TestingFinalizeReason.OfflineLimit; }
        }
        private void ConnectivityChanged(bool online)
        {
            lock (gate)
            {
                if (disposed) return;
                CheckDeadlines();
                if (!online && status == TestingSessionStatus.Running)
                {
                    elapsed = ReadElapsed(); offlineStart = clock.Monotonic; offlinePeriodStart = clock.UtcNow;
                    status = TestingSessionStatus.Offline;
                }
                else if (online && status == TestingSessionStatus.Offline)
                { CloseOfflinePeriod(); segmentStart = clock.Monotonic; status = TestingSessionStatus.Running; }
            }
        }
        private void CloseOfflinePeriod()
        {
            if (!offlinePeriodStart.HasValue) return;
            DateTime end = clock.UtcNow < offlinePeriodStart.Value ? offlinePeriodStart.Value : clock.UtcNow;
            offlinePeriods.Add(new TestingOfflinePeriod(offlinePeriodStart.Value, end, (clock.Monotonic - offlineStart).Ticks));
            offlinePeriodStart = null;
        }
        private TimeSpan ReadElapsed()
        {
            TimeSpan value = elapsed + (!disposed && status == TestingSessionStatus.Running ? clock.Monotonic - segmentStart : TimeSpan.Zero);
            return value > Duration ? Duration : value;
        }
        public void RestartCurrent()
        {
            lock (gate) { RequireRunning(); grades[current] = null; }
        }
        public void NextProject(IEnumerable<ExamTaskGrade> results)
        {
            lock (gate)
            {
                RequireRunning();
                if (current == 6) throw new InvalidOperationException("Use Submit on the last project.");
                Lock(current, results); current++; states[current] = TestingProjectState.Active;
            }
        }
        public void BeginSubmit(IEnumerable<ExamTaskGrade> currentResults)
        {
            lock (gate)
            {
                EnsureUsable(); CheckDeadlines();
                if (status == TestingSessionStatus.Ready || status == TestingSessionStatus.Completed || states[current] == TestingProjectState.Locked)
                    throw new InvalidOperationException("Session cannot begin submission.");
                if (status == TestingSessionStatus.Offline) CloseOfflinePeriod();
                Lock(current, currentResults);
                elapsed = ReadElapsed(); status = TestingSessionStatus.Submitting;
                if (reason == TestingFinalizeReason.None) reason = TestingFinalizeReason.Submitted;
            }
        }
        public void RecordUnvisitedResults(int index, IEnumerable<ExamTaskGrade> results)
        {
            lock (gate)
            {
                EnsureUsable();
                if (status != TestingSessionStatus.Submitting || index <= current || index >= 7 || states[index] != TestingProjectState.NotStarted)
                    throw new InvalidOperationException("Only an unvisited project may be recorded during submission.");
                Lock(index, results);
            }
        }
        private void Lock(int index, IEnumerable<ExamTaskGrade> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            var list = results.ToList(); var project = Definition.Projects[index];
            if (list.Any(g => g == null || g.ProjectId != project.ProjectId || !project.TaskIds.Contains(g.TaskId) ||
                g.Result == null || g.Result.TaskId != g.TaskId || !Enum.IsDefined(typeof(TaskGradeOutcome), g.Result.Outcome)) ||
                list.Select(g => g.TaskId).Distinct().Count() != list.Count)
                throw new InvalidDataException("Locked grades must belong to the selected project/tasks.");
            grades[index] = list; states[index] = TestingProjectState.Locked;
        }
        public IReadOnlyList<ExamTaskGrade> GetLockedResults()
        {
            lock (gate) return grades.Where(g => g != null).SelectMany(g => g).ToList().AsReadOnly();
        }
        public TestingSessionSnapshot CaptureState()
        {
            lock (gate)
            {
                EnsureUsable(); CheckDeadlines();
                var periods = offlinePeriods.ToList();
                if (status == TestingSessionStatus.Offline && offlinePeriodStart.HasValue)
                    periods.Add(new TestingOfflinePeriod(offlinePeriodStart.Value, null, (clock.Monotonic - offlineStart).Ticks));
                return new TestingSessionSnapshot(status, reason, current, ReadElapsed(), states, GetLockedResults(), result,
                    status == TestingSessionStatus.Offline ? clock.Monotonic - offlineStart : TimeSpan.Zero, startedUtc, completedUtc, periods, historyIncomplete);
            }
        }
        public ExamResult Complete(Action<string> logError)
        {
            lock (gate)
            {
                EnsureUsable();
                if (status != TestingSessionStatus.Submitting || states.Any(s => s != TestingProjectState.Locked))
                    throw new InvalidOperationException("All projects must be graded before completion.");
                result = new ExamScorer(logError).Score(Definition, GetLockedResults());
                completedUtc = clock.UtcNow;
                status = TestingSessionStatus.Completed;
                return result;
            }
        }
        private void RequireRunning()
        {
            EnsureUsable(); CheckDeadlines();
            if (status != TestingSessionStatus.Running) throw new InvalidOperationException("Testing inputs are locked.");
        }
        private void EnsureUsable() { if (disposed) throw new ObjectDisposedException(nameof(TestingSession)); }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                elapsed = ReadElapsed(); disposed = true;
                connectivity.ConnectivityChanged -= ConnectivityChanged;
            }
        }
    }
    public sealed class TestingOfflinePeriod
    {
        public DateTime StartedUtc { get; }
        public DateTime? EndedUtc { get; }
        public long DurationTicks { get; }
        public TestingOfflinePeriod(DateTime startedUtc, DateTime? endedUtc, long durationTicks)
        {
            if (startedUtc.Kind != DateTimeKind.Utc || endedUtc.HasValue && (endedUtc.Value.Kind != DateTimeKind.Utc || endedUtc < startedUtc) || durationTicks < 0)
                throw new InvalidDataException("Invalid offline period.");
            StartedUtc = startedUtc; EndedUtc = endedUtc; DurationTicks = durationTicks;
        }
    }
    public sealed class TestingSessionSnapshot
    {
        public TestingSessionStatus Status { get; }
        public TestingFinalizeReason FinalizeReason { get; }
        public int CurrentIndex { get; }
        public TimeSpan Elapsed { get; }
        public IReadOnlyList<TestingProjectState> ProjectStates { get; }
        public IReadOnlyList<ExamTaskGrade> LockedResults { get; }
        public ExamResult Result { get; }
        public TimeSpan OfflineElapsed { get; }
        public DateTime? StartedUtc { get; }
        public DateTime? CompletedUtc { get; }
        public IReadOnlyList<TestingOfflinePeriod> OfflinePeriods { get; }
        public bool HistoryIncomplete { get; }
        public TestingSessionSnapshot(TestingSessionStatus status, TestingFinalizeReason reason, int index,
            TimeSpan elapsed, IEnumerable<TestingProjectState> states, IEnumerable<ExamTaskGrade> grades, ExamResult result, TimeSpan offlineElapsed = default(TimeSpan), DateTime? startedUtc = null,
            DateTime? completedUtc = null, IEnumerable<TestingOfflinePeriod> offlinePeriods = null, bool historyIncomplete = false)
        {
            Status = status; FinalizeReason = reason; CurrentIndex = index; Elapsed = elapsed; Result = result; OfflineElapsed = offlineElapsed;
            StartedUtc = startedUtc; CompletedUtc = completedUtc; HistoryIncomplete = historyIncomplete;
            OfflinePeriods = (offlinePeriods ?? new TestingOfflinePeriod[0]).ToList().AsReadOnly();
            ProjectStates = states.ToList().AsReadOnly(); LockedResults = grades.ToList().AsReadOnly();
        }
    }

}
