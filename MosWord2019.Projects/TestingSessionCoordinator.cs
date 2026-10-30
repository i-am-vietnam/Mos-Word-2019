using System;
using System.Collections.Generic;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
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
        private bool disposed, faulted, entered, busyWarning, resumeEntryPending, attached, savedOnlyFinalization;
        private TimeSpan? automaticBusySince;
        public bool NeedsRecovery => faulted;
        public bool CleanupPending { get; private set; }
        public event Action WordDialogRequired;
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
            attached = true; Session.Start(); Save(); StartHeartbeat(); ApplyLock(); StateChanged?.Invoke();
        }
        public void Resume()
        {
            EnsureUsable(); attached = true; Session.Tick();
            if (Session.Status == TestingSessionStatus.Submitting) { SubmitSaved(); return; }
            resumeEntryPending = true; StartHeartbeat(); EnterCurrent(); ApplyLock(); Save(); StateChanged?.Invoke();
        }
        private void EnterCurrent()
        { runtime.Enter(Current, resumeEntryPending); entered = true; resumeEntryPending = false; }
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
                if (!entered) { EnterCurrent(); ApplyLock(); StateChanged?.Invoke(); return; }
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
                else { EnterCurrent(); lastSave = clock.Monotonic; ApplyLock(); }
                busyWarning = false; StateChanged?.Invoke();
            }
            catch (Exception ex) when (WordBusyException.IsBusy(ex)) { Busy(); throw; }
            catch (WordReopenPendingException) { throw; }
            catch { faulted = true; throw; }
        }
        public void Restart()
        {
            RequireRunning(); runtime.SetInputEnabled(false); if (entered) runtime.Restart(Current); else EnterCurrent();
            Session.RestartCurrent(); Save(); lastSave = clock.Monotonic; ApplyLock(); StateChanged?.Invoke();
        }
        public void Submit()
        {
            EnsureUsable();
            if (Session.Status == TestingSessionStatus.Ready || Session.Status == TestingSessionStatus.Completed) throw new InvalidOperationException("No active exam.");
            try
            {
                if (!savedOnlyFinalization) runtime.SetInputEnabled(false);
                if (Session.ProjectStates[Session.CurrentIndex] != TestingProjectState.Locked)
                {
                    if (entered) runtime.LeaveAndGrade(grades => { Session.BeginSubmit(grades); Save(); });
                    else { Session.BeginSubmit(runtime.GradeSaved(Current)); Save(); }
                }
                entered = false; Finish();
            }
            catch (Exception ex) when (WordBusyException.IsBusy(ex)) { Busy(); throw; }
            catch (WordReopenPendingException) { throw; }
            catch { faulted = true; throw; }
        }
        public void SubmitSaved() { EnsureUsable(); savedOnlyFinalization = true; entered = false; Submit(); }
        private void Finish()
        {
            heartbeat?.Dispose(); heartbeat = null;
            for (int i = Session.CurrentIndex + 1; i < 7; i++)
                if (Session.ProjectStates[i] != TestingProjectState.Locked)
                { Session.RecordUnvisitedResults(i, runtime.GradeSaved(prepared.Definition.Projects[i].ProjectId)); Save(); }
            Session.Complete(log); Save(); store.PersistCompletedResult(); // Never clean recovery inputs before the result is durable.
            try { runtime.Close(); if (!CleanupPending) store.ArchiveCompleted(); }
            catch (Exception ex) { CleanupPending = true; log("Completed Testing cleanup pending: " + ex.Message); }
            Completed?.Invoke(); StateChanged?.Invoke();
        }
        private void Busy()
        {
            if (!busyWarning) { WordDialogRequired?.Invoke(); busyWarning = true; }
            if (Session.Status == TestingSessionStatus.Running)
                try { ApplyLock(); } catch (Exception ex) when (WordBusyException.IsBusy(ex)) { }
        }
        public void SetWindowBounds(int left, int top, int width, int height)
        { (runtime as IWordWindowLayout)?.SetWindowBounds(left, top, width, height); }
        private void ApplyLock() => runtime.SetInputEnabled(!Session.InputLocked);
        public void Tick(bool observe = true)
        {
            EnsureUsable(); if (!attached || Session.Status == TestingSessionStatus.Ready || Session.Status == TestingSessionStatus.Completed) return;
            Session.Tick();
            if (!savedOnlyFinalization) try { ApplyLock(); } catch (Exception ex) when (WordBusyException.IsBusy(ex)) { Busy(); }
            StateChanged?.Invoke();
            if (Session.Status == TestingSessionStatus.Submitting)
            {
                if (!automaticBusySince.HasValue) automaticBusySince = clock.Monotonic;
                if (clock.Monotonic - automaticBusySince.Value >= TimeSpan.FromSeconds(30))
                {
                    savedOnlyFinalization = true;
                    var recovery = runtime as ITestingForceRecovery ?? throw new InvalidOperationException("Verified Word force recovery is unavailable.");
                    try { recovery.ForceCloseOwnedWord(); }
                    catch (Exception ex) { CleanupPending = true; log("Verified force cleanup pending; grading saved work: " + ex.Message); }
                    entered = false; Submit(); return;
                }
                try { Submit(); } catch (Exception ex) when (WordBusyException.IsBusy(ex)) { }
                return;
            }
            if (!entered && Session.Status == TestingSessionStatus.Running)
            {
                try { EnterCurrent(); ApplyLock(); Save(); }
                catch (Exception ex) when (WordBusyException.IsBusy(ex)) { Busy(); return; }
            }
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
