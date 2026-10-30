using System;
using System.Collections.Generic;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
namespace MosWord2019.Projects
{
    /// <summary>Serial owner-thread orchestration. UI receives state/results, never live grader diagnostics.</summary>
    public sealed class TestingSessionCoordinator : IDisposable
    {
        private readonly PreparedTestingSession prepared;
        private readonly TestingSessionStateStore store;
        private readonly ITestingClock clock;
        private readonly ITestingProjectRuntime runtime;
        private readonly Action preflight;
        private readonly Action<string> log;
        private TimeSpan lastSave;
        private TestingHeartbeat heartbeat;
        private bool disposed, faulted, entered;
        public TestingSession Session { get; }
        public event Action StateChanged;
        public event Action Completed;
        public TestingSessionCoordinator(PreparedTestingSession prepared, TestingSession session, TestingSessionStateStore store,
            ITestingClock clock, ITestingProjectRuntime runtime, Action preflight, Action<string> log)
        {
            this.prepared = prepared ?? throw new ArgumentNullException(nameof(prepared)); Session = session ?? throw new ArgumentNullException(nameof(session));
            this.store = store ?? throw new ArgumentNullException(nameof(store)); this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }
        private string Current => prepared.Definition.Projects[Session.CurrentIndex].ProjectId;
        private void Save() => store.SaveHeartbeat(prepared, Session);
        public void Start()
        {
            EnsureUsable();
            if (Session.Status != TestingSessionStatus.Ready) throw new InvalidOperationException("Exam already started.");
            preflight(); runtime.Enter(Current, false); entered = true; // No timer until preflight and Word entry succeed.
            Session.Start(); Save(); StartHeartbeat(); ApplyLock(); StateChanged?.Invoke();
        }
        public void Resume()
        {
            EnsureUsable();
            if (Session.Status == TestingSessionStatus.Submitting) { FinalizeSaved(); return; }
            runtime.Enter(Current, true); entered = true; StartHeartbeat(); ApplyLock(); Save(); StateChanged?.Invoke();
        }
        private void StartHeartbeat()
        { lastSave = clock.Monotonic; heartbeat = new TestingHeartbeat(prepared, Session, store, clock); }
        private void EnsureUsable()
        { if (disposed || faulted) throw new InvalidOperationException("Testing session must be recovered before further operations."); }
        private void RequireRunning()
        { EnsureUsable(); Session.Tick(); if (Session.InputLocked) throw new InvalidOperationException("Testing inputs are locked."); }
        public void Next()
        {
            RequireRunning(); if (Session.CurrentIndex == 6) throw new InvalidOperationException("Use Submit on the last project.");
            try
            {
                runtime.SetInputEnabled(false);
                runtime.LeaveAndGrade(grades =>
                {
                    Session.Tick();
                    if (Session.Status == TestingSessionStatus.Submitting) Session.BeginSubmit(grades);
                    else Session.NextProject(grades);
                    Save(); // Persist locked grades before outputs/assets are cleaned.
                });
                entered = false;
                if (Session.Status == TestingSessionStatus.Submitting) Finish();
                else { runtime.Enter(Current, false); entered = true; lastSave = clock.Monotonic; ApplyLock(); }
                StateChanged?.Invoke();
            }
            catch { faulted = true; throw; }
        }
        public void Restart()
        {
            RequireRunning(); runtime.SetInputEnabled(false); runtime.Restart(Current);
            Session.RestartCurrent(); Save(); lastSave = clock.Monotonic; ApplyLock(); StateChanged?.Invoke();
        }
        public void Submit()
        {
            EnsureUsable();
            if (Session.Status == TestingSessionStatus.Ready || Session.Status == TestingSessionStatus.Completed) throw new InvalidOperationException("No active exam.");
            try
            {
                runtime.SetInputEnabled(false);
                if (Session.ProjectStates[Session.CurrentIndex] != TestingProjectState.Locked)
                {
                    if (entered) runtime.LeaveAndGrade(grades => { Session.BeginSubmit(grades); Save(); });
                    else { Session.BeginSubmit(runtime.GradeSaved(Current)); Save(); }
                }
                entered = false; Finish();
            }
            catch { faulted = true; throw; }
        }
        private void FinalizeSaved() { entered = false; Submit(); }
        private void Finish()
        {
            heartbeat?.Dispose(); heartbeat = null;
            for (int i = Session.CurrentIndex + 1; i < 7; i++)
                if (Session.ProjectStates[i] != TestingProjectState.Locked)
                { Session.RecordUnvisitedResults(i, runtime.GradeSaved(prepared.Definition.Projects[i].ProjectId)); Save(); }
            Session.Complete(log); Save(); // Never clean recovery inputs before the result is durable.
            runtime.Close(); store.ArchiveCompleted(); Completed?.Invoke(); StateChanged?.Invoke();
        }
        private void ApplyLock() => runtime.SetInputEnabled(!Session.InputLocked);
        public void Tick(bool observe = true)
        {
            EnsureUsable(); if (Session.Status == TestingSessionStatus.Ready || Session.Status == TestingSessionStatus.Completed) return;
            Session.Tick(); ApplyLock(); StateChanged?.Invoke();
            if (Session.Status == TestingSessionStatus.Submitting) { Submit(); return; }
            if (!observe) return; // Deadlines and lock above always run, even during observation backoff.
            runtime.Observe();
            if (Session.Status == TestingSessionStatus.Running)
            {
                // Observe also runs while offline; a reopened window inherits the current input lock.
                if (clock.Monotonic - lastSave >= TimeSpan.FromSeconds(60))
                { runtime.AutosaveCurrent(); lastSave = clock.Monotonic; }
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; heartbeat?.Dispose();
            try { runtime.SetInputEnabled(false); }
            catch (Exception ex) { log("Testing input lock deferred: " + ex.Message); }
            try { runtime.Dispose(); }
            catch (Exception ex) { log("Testing disposal deferred: " + ex.Message); }
            Session.Dispose();
        }
    }
}
