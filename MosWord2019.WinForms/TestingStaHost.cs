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
        public event Action<TestingSessionSnapshot> StateChanged;
        public event Action<string> Warning;
        public TestingStaHost(Func<TestingSessionCoordinator> create, IConnectivityMonitor network, Action<string> log = null)
        {
            this.log = log ?? (s => AppLogger.Write(s));
            thread = new Thread(() =>
            {
                using (var control = new Control())
                using (var timer = new System.Windows.Forms.Timer { Interval = 250 })
                {
                    try
                    {
                        dispatcher = control; control.CreateControl(); engine = create();
                        engine.StateChanged += () => StateChanged?.Invoke(engine.Session.CaptureState());
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
        }
        private readonly Stopwatch pacing = Stopwatch.StartNew();
        private TimeSpan retryAfter;
        private bool failureLogged;
        private readonly Stopwatch rejection = new Stopwatch();
        private bool rejectionLogged;
        private void Tick()
        {
            if (executing || disposed) return;
            bool observe = pacing.Elapsed >= retryAfter;
            executing = true;
            try { engine.Tick(observe); if (observe) { rejection.Reset(); rejectionLogged = false; failureLogged = false; } }
            catch (WordBusyException ex)
            {
                if (!rejection.IsRunning) rejection.Start(); retryAfter = pacing.Elapsed + TimeSpan.FromMilliseconds(250);
                if (!rejectionLogged && rejection.ElapsedMilliseconds >= 5000)
                { log("Testing observation delayed: " + ex.Message); rejectionLogged = true; }
            }
            catch (Exception ex) { retryAfter = pacing.Elapsed + TimeSpan.FromSeconds(5);
                if (!failureLogged) { log("Testing tick deferred: " + ex.Message); Warning?.Invoke("Testing needs attention; your saved work is retained. / Bài thi cần kiểm tra; bài đã lưu được giữ lại."); failureLogged = true; } }
            finally { executing = false; Drain(); }
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
            Post(() => { try { done.SetResult(command(engine)); } catch (Exception ex) { done.SetException(ex); } });
            if (await Task.WhenAny(done.Task, stopped.Task, disposing.Task).ConfigureAwait(false) != done.Task) throw new InvalidOperationException("Testing STA has stopped.");
            return await done.Task.ConfigureAwait(false);
        }
        public Task<bool> StartAsync() => InvokeAsync(e => { e.Start(); return true; });
        public Task<bool> ResumeAsync() => InvokeAsync(e => { e.Resume(); return true; });
        public Task<bool> SubmitAsync() => InvokeAsync(e => { e.Submit(); return true; });
        public static TestingStaHost Create(PreparedTestingSession prepared, TestingSessionStateStore store, SessionRecoveryJournal journal,
            IEnumerable<ProjectPackage> packages, TestingRecoveryRecord recovery = null, IConnectivityMonitor network = null)
        {
            network = network ?? new AlwaysOnlineMonitor(); TestingStaHost host = null;
            host = new TestingStaHost(() =>
            {
                var clock = new StopwatchTestingClock();
                var session = recovery == null ? new TestingSession(prepared.SessionId, prepared.Definition, clock, network) : recovery.Restore(clock, network);
                var workspace = new TestingWorkspaceService(prepared.SessionId, journal, resumeExisting: true);
                if (recovery != null) workspace.Preflight(prepared, packages, false);
                var word = new WordController();
                var runner = new TestingProjectRunner(workspace, word, prepared, store, new MosWord2019.Core.Services.WordGradingService(), s => AppLogger.Write(s),
                    () => word.BeginShortCallBudget(250));
                runner.DocumentReopened += () => host?.Warning?.Invoke("Word was closed. The latest checkpoint has been reopened; the timer keeps running. / Word đã đóng. Đã mở lại checkpoint gần nhất; thời gian vẫn chạy.");
                var coordinator = new TestingSessionCoordinator(prepared, session, store, clock, runner,
                    () => workspace.Preflight(prepared, packages), s => AppLogger.Write(s));
                coordinator.Completed += () => { journal.ReleaseSession(prepared.SessionId); journal.DeferRecovery(false); journal.Recover(); };
                return coordinator;
            }, network);
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
