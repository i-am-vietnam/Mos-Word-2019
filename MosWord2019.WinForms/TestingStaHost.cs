using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using MosWord2019.Word;
namespace MosWord2019
{
    public enum TestingWarningCode { DocumentReopened, ObservationDelayed, RecoveryRequired }
    /// <summary>Single background STA with a message pump; all Testing Word calls and grading stay here.</summary>
    public sealed class TestingStaHost : IDisposable
    {
        private readonly Thread thread;
        private readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<Action> commands = new Queue<Action>();
        private readonly TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> disposing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Control dispatcher;
        private TestingSessionCoordinator engine;
        private bool executing, disposed;
        private readonly Action<string> log;
        private readonly WordWarmup warmup;
        private TimeSpan nextWarmCheck;
        internal WordWarmupState WarmupState => warmup?.State ?? WordWarmupState.NotStarted;
        internal Task PrewarmAsync() => warmup?.EnsureAsync() ?? Task.CompletedTask;
        public event Action<TestingSessionSnapshot> StateChanged;
        public event Action<TestingWarningCode> Warning;
        public event Action WordDialogRequired;
#if DEBUG
        private readonly DebugConnectivityMonitor debugNetwork;
        internal bool CanSimulateOffline => debugNetwork != null;
        internal bool IsDebugOffline => debugNetwork != null && !debugNetwork.IsOnline;
        internal Task<bool> SetDebugOfflineAsync(bool offline) => InvokeAsync(c =>
        {
            if (debugNetwork == null) throw new InvalidOperationException("This host has no Debug connectivity simulator.");
            debugNetwork.SetOnline(!offline);
            c.Tick(false); // Apply the existing lock/overlay/deadline path on the owning STA immediately.
            return true;
        });
#endif
        public TestingStaHost(Func<TestingSessionCoordinator> create, IConnectivityMonitor network, Action<string> log = null, Action warmWord = null, Func<bool> wordAlive = null)
        {
            this.log = log ?? (s => AppLogger.Write(s));
#if DEBUG
            debugNetwork = network as DebugConnectivityMonitor;
#endif
            thread = new Thread(() =>
            {
                using (var control = new Control())
                using (var timer = new System.Windows.Forms.Timer { Interval = 250 })
                {
                    try
                    {
                        dispatcher = control; control.CreateControl(); engine = create();
                        engine.StateChanged += () => StateChanged?.Invoke(engine.Session.CaptureState());
                        engine.WordDialogRequired += () => WordDialogRequired?.Invoke();
                        Action<bool> connectivity = _ => Post(Tick);
                        network.ConnectivityChanged += connectivity;
                        timer.Tick += (s, e) => { if (!executing) Tick(); }; timer.Start(); ready.TrySetResult(true);
                        try { Application.Run(); }
                        finally { timer.Stop(); network.ConnectivityChanged -= connectivity; engine.Dispose(); }
                    }
                    catch (Exception ex) { ready.TrySetException(ex); this.log("Testing STA stopped; recovery retained: " + ex.Message); }
                    finally { stopped.TrySetResult(true); }
                }
            }) { IsBackground = true, Name = "Testing Word STA" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (warmWord != null) warmup = new WordWarmup(() => InvokeAsync(c => { warmWord(); return true; }), () => InvokeAsync(c => wordAlive != null && wordAlive()));
        }
        private readonly Stopwatch pacing = Stopwatch.StartNew();
        private TimeSpan retryAfter;
        private bool failureLogged;
        private bool recoveryReported;
        private readonly Stopwatch rejection = new Stopwatch();
        private bool rejectionLogged;
        private void Tick()
        {
            if (executing || disposed) return;
            if (engine.Session.Status == TestingSessionStatus.Ready && pacing.Elapsed >= nextWarmCheck)
            {
                nextWarmCheck = pacing.Elapsed + TimeSpan.FromSeconds(1); _ = CheckWarmWord();
            }
            bool observe = pacing.Elapsed >= retryAfter;
            executing = true;
            try { engine.Tick(observe); if (observe) { rejection.Reset(); rejectionLogged = false; failureLogged = false; } }
            catch (Exception ex) when (WordBusyException.IsBusy(ex))
            {
                if (!rejection.IsRunning) rejection.Start(); retryAfter = pacing.Elapsed + TimeSpan.FromMilliseconds(250);
                if (!rejectionLogged && rejection.ElapsedMilliseconds >= 5000)
                { log("Testing observation delayed: " + ex.Message); rejectionLogged = true; }
            }
            catch (Exception ex) { retryAfter = pacing.Elapsed + TimeSpan.FromSeconds(5);
                if (!failureLogged) { log("Testing tick deferred: " + ex.Message);
                    if (!engine.NeedsRecovery) Warning?.Invoke(TestingWarningCode.ObservationDelayed); failureLogged = true; }
                ReportRecovery(); }
            finally { executing = false; Drain(); }
        }
        private void ReportRecovery()
        {
            if (engine.NeedsRecovery && !recoveryReported)
            { recoveryReported = true; Warning?.Invoke(TestingWarningCode.RecoveryRequired); }
        }
        private void Post(Action action)
        {
            if (disposed) return;
            try { dispatcher.BeginInvoke((Action)(() => { commands.Enqueue(action); Drain(); })); }
            catch (InvalidOperationException) { if (!disposed) throw; }
        }
        private void Drain()
        {
            if (executing) return;
            executing = true;
            try { while (commands.Count > 0) commands.Dequeue()(); }
            finally { executing = false; }
        }
        public async Task<T> InvokeAsync<T>(Func<TestingSessionCoordinator, T> command)
        {
            await ready.Task.ConfigureAwait(false);
            if (disposed) throw new ObjectDisposedException(nameof(TestingStaHost));
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() => { try { done.SetResult(command(engine)); } catch (Exception ex) {
                ReportRecovery();
                done.SetException(ex); } });
            if (await Task.WhenAny(done.Task, stopped.Task, disposing.Task).ConfigureAwait(false) != done.Task) throw new InvalidOperationException("Testing STA has stopped.");
            return await done.Task.ConfigureAwait(false);
        }
        private async Task CheckWarmWord()
        {
            try { if (warmup != null) await warmup.CheckAsync().ConfigureAwait(false); }
            catch (Exception ex) { log("Testing prewarm check failed; Start will retry: " + ex.Message); }
        }
        public async Task<bool> StartAsync()
        {
            try { await PrewarmAsync().ConfigureAwait(false); }
            catch (Exception ex) { log("Testing prewarm unavailable; ordinary Start will retry: " + ex.Message); }
            return await InvokeAsync(e => { e.Start(); return true; }).ConfigureAwait(false);
        }
        public Task<bool> ResumeAsync() => InvokeAsync(e => { e.Resume(); return true; });
        public Task<bool> SubmitAsync() => InvokeAsync(e => { e.Submit(); return true; });
        public Task<bool> SubmitSavedAsync() => InvokeAsync(e => { e.SubmitSaved(); return true; });
        internal static IConnectivityMonitor CreateConnectivityMonitor()
        {
#if DEBUG
            return new DebugConnectivityMonitor();
#else
            return new AlwaysOnlineMonitor();
#endif
        }
        public static TestingStaHost Create(PreparedTestingSession prepared, TestingSessionStateStore store, SessionRecoveryJournal journal,
            IEnumerable<ProjectPackage> packages, TestingRecoveryRecord recovery = null, IConnectivityMonitor network = null)
        {
            network = network ?? CreateConnectivityMonitor(); TestingStaHost host = null; WordController ownedWord = null;
            host = new TestingStaHost(() =>
            {
                var clock = new StopwatchTestingClock();
                var session = recovery == null ? new TestingSession(prepared.SessionId, prepared.Definition, clock, network) : recovery.Restore(clock, network);
                var workspace = new TestingWorkspaceService(prepared.SessionId, journal, resumeExisting: true);
                if (recovery != null) workspace.Preflight(prepared, packages, false);
                var word = new WordController(); ownedWord = word;
                var runner = new TestingProjectRunner(workspace, word, prepared, store, new MosWord2019.Core.Services.WordGradingService(), s => AppLogger.Write(s),
                    () => word.BeginShortCallBudget(250), () =>
                    {
                        var identity = word.OwnedProcessIdentity;
                        if (identity != null) new OwnedWordProcessRecovery().Stop(identity, Environment.MachineName);
                    });
                runner.DocumentReopened += () => host?.Warning?.Invoke(TestingWarningCode.DocumentReopened);
                var coordinator = new TestingSessionCoordinator(prepared, session, store, clock, runner,
                    () => workspace.Preflight(prepared, packages), s => AppLogger.Write(s));
                coordinator.Completed += () => { if (coordinator.CleanupPending) journal.DeferRecovery(true);
                    else { journal.ReleaseSession(prepared.SessionId); journal.DeferRecovery(false); journal.Recover(); } };
                return coordinator;
            }, network, warmWord: () => ownedWord.Prewarm(), wordAlive: () => ownedWord.IsApplicationRunning);
            return host;
        }
        public void Dispose()
        {
            if (disposed) return;
            try
            {
                ready.Task.GetAwaiter().GetResult();
                if (Thread.CurrentThread == thread) { engine.Dispose(); Application.ExitThread(); return; }
                var close = InvokeAsync(e => { e.Dispose(); Application.ExitThread(); return true; });
                if (!close.Wait(2000)) throw new TimeoutException("Testing STA cleanup deferred to journal recovery.");
                close.GetAwaiter().GetResult();
            }
            catch (Exception ex) { log("Testing host disposal retained journal: " + ex.Message); }
            finally { disposed = true; disposing.TrySetResult(true); if (Thread.CurrentThread != thread) thread.Join(2000); }
        }
    }
}
