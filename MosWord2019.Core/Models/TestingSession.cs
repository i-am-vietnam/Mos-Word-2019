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
    public enum TestingFinalizeReason { None, Submitted, Timeout, OfflineLimit }

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

        public void Start()
        {
            lock (gate)
            {
                EnsureUsable();
                if (status != TestingSessionStatus.Ready) throw new InvalidOperationException("Session already started.");
                segmentStart = offlineStart = clock.Monotonic;
                status = connectivity.IsOnline ? TestingSessionStatus.Running : TestingSessionStatus.Offline;
                states[0] = TestingProjectState.Active;
            }
        }
        public void Tick() { lock (gate) { EnsureUsable(); CheckDeadlines(); } }
        private void CheckDeadlines()
        {
            if (status == TestingSessionStatus.Running && ReadElapsed() >= Duration)
            { elapsed = Duration; status = TestingSessionStatus.Submitting; reason = TestingFinalizeReason.Timeout; }
            if (status == TestingSessionStatus.Offline && clock.Monotonic - offlineStart >= OfflineLimit)
            { status = TestingSessionStatus.Submitting; reason = TestingFinalizeReason.OfflineLimit; }
        }
        private void ConnectivityChanged(bool online)
        {
            lock (gate)
            {
                if (disposed) return;
                CheckDeadlines();
                if (!online && status == TestingSessionStatus.Running)
                {
                    elapsed = ReadElapsed(); offlineStart = clock.Monotonic;
                    status = TestingSessionStatus.Offline;
                }
                else if (online && status == TestingSessionStatus.Offline)
                { segmentStart = clock.Monotonic; status = TestingSessionStatus.Running; }
            }
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
                return new TestingSessionSnapshot(status, reason, current, ReadElapsed(), states, GetLockedResults(), result);
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
    public sealed class TestingSessionSnapshot
    {
        public TestingSessionStatus Status { get; }
        public TestingFinalizeReason FinalizeReason { get; }
        public int CurrentIndex { get; }
        public TimeSpan Elapsed { get; }
        public IReadOnlyList<TestingProjectState> ProjectStates { get; }
        public IReadOnlyList<ExamTaskGrade> LockedResults { get; }
        public ExamResult Result { get; }
        internal TestingSessionSnapshot(TestingSessionStatus status, TestingFinalizeReason reason, int index,
            TimeSpan elapsed, IEnumerable<TestingProjectState> states, IEnumerable<ExamTaskGrade> grades, ExamResult result)
        {
            Status = status; FinalizeReason = reason; CurrentIndex = index; Elapsed = elapsed; Result = result;
            ProjectStates = states.ToList().AsReadOnly(); LockedResults = grades.ToList().AsReadOnly();
        }
    }

}
