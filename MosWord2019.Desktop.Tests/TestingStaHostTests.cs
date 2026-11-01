using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;
using MosWord2019.Word;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TestingStaHostTests
    {
        [DataTestMethod]
        [DataRow(false)] [DataRow(true)]
        public async Task RulesPrewarmDoesNotStartTimerAndStartJoinsIt(bool fail)
        {
            using (var folder = new TestWorkspace())
            using (var gate = new ManualResetEventSlim())
            {
                var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en");
                var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"));
                var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, "en");
                var network = new AlwaysOnlineMonitor(); var clock = new TestingSessionTests.FakeClock();
                TestingSession session = null; int activations = 0, warmThread = 0; TimeSpan rulesElapsed = TimeSpan.MinValue;
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var host = new MosWord2019.TestingStaHost(() =>
                {
                    session = new TestingSession(prepared.SessionId, prepared.Definition, clock, network);
                    return new TestingSessionCoordinator(prepared, session, store, clock, new EmptyRuntime(), () => { }, _ => { });
                }, network, _ => { }, () =>
                {
                    activations++; warmThread = Thread.CurrentThread.ManagedThreadId; entered.TrySetResult(true);
                    if (!gate.Wait(5000)) throw new TimeoutException("test gate");
                    clock.Advance(120); rulesElapsed = session.Elapsed;
                    if (fail) throw new InvalidOperationException("warm failure");
                }, () => true))
                {
                    try
                    {
                        var warming = host.PrewarmAsync();
                        Assert.AreEqual(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(3000)));
                        var start = host.StartAsync(); Assert.IsFalse(start.IsCompleted);
                        gate.Set(); if (fail) await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => warming); else await warming;
                        await start;
                        Assert.AreEqual(1, activations); Assert.AreEqual(TimeSpan.Zero, rulesElapsed);
                        Assert.AreEqual(TestingSessionStatus.Running, await host.InvokeAsync(c => c.Session.Status));
                        Assert.AreEqual(TimeSpan.Zero, await host.InvokeAsync(c => c.Session.Elapsed));
                        Assert.AreEqual(warmThread, await host.InvokeAsync(c => Thread.CurrentThread.ManagedThreadId));
                    }
                    finally { gate.Set(); }
                }
            }
        }
        [TestMethod]
        public void SyntheticConnectivityTypesAndCommandsAreAbsentFromRelease()
        {
            var assembly = typeof(MosWord2019.TestingStaHost).Assembly;
            var type = assembly.GetType("MosWord2019.DebugConnectivityMonitor");
            var command = typeof(MosWord2019.TestingStaHost).GetMethod("SetDebugOfflineAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
#if DEBUG
            Assert.IsNotNull(type); Assert.IsNotNull(command);
#else
            Assert.IsNull(type); Assert.IsNull(command);
#endif
        }
        [TestMethod]
        public async Task HostOwnsPumpedBackgroundStaAndSerializesCommands()
        {
            using (var folder = new TestWorkspace())
            {
                var clock = new StopwatchTestingClock(); var network = new AlwaysOnlineMonitor();
                var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"));
                var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"), "en");
                using (var host = new MosWord2019.TestingStaHost(() => new TestingSessionCoordinator(prepared,
                    new TestingSession(prepared.SessionId, prepared.Definition, clock, network), store, clock, new EmptyRuntime(), () => { }, _ => { }), network))
                {
                    int owner = await host.InvokeAsync(e => { Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState()); return Thread.CurrentThread.ManagedThreadId; });
                    int count = 0;
                    var first = host.InvokeAsync(e => { count++; return Thread.CurrentThread.ManagedThreadId; });
                    var second = host.InvokeAsync(e => { count++; return Thread.CurrentThread.ManagedThreadId; });
                    Assert.AreEqual(owner, await first); Assert.AreEqual(owner, await second); Assert.AreEqual(2, count);
                }
            }
        }
        [TestMethod]
        public void DestroyedOwnedWindowIsANoOpAndOtherProcessWindowIsProtected()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var process = Process.GetCurrentProcess())
                    using (var handle = new SafeProcessHandle(process.Handle, false))
                    using (var owned = new WinApiProcessHelper.OwnedProcess(process.Id, handle))
                    {
                        var form = new Form(); IntPtr window = form.Handle;
                        Assert.IsTrue(owned.SetInputEnabled(window, false)); Assert.IsFalse(IsWindowEnabled(window));
                        Assert.IsTrue(owned.SetInputEnabled(window, true)); Assert.IsTrue(IsWindowEnabled(window));
                        using (var other = new WinApiProcessHelper.OwnedProcess(process.Id + 1, new SafeProcessHandle(process.Handle, false)))
                            Assert.ThrowsException<InvalidOperationException>(() => other.SetInputEnabled(window, false));
                        Assert.IsTrue(IsWindowEnabled(window)); form.Dispose();
                        Assert.IsFalse(owned.SetInputEnabled(window, false));
                    }
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        private sealed class EmptyRuntime : ITestingProjectRuntime
        {
            internal int ObserveFailures;
            internal bool GradeFails;
            internal readonly TaskCompletionSource<bool> Observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Enter(string id, bool resume) { }
            public void AutosaveCurrent() { }
            public void Observe() { if (Interlocked.Decrement(ref ObserveFailures) >= 0) throw new IOException("transient tick failure"); Observed.TrySetResult(true); }
            public void SetInputEnabled(bool enabled) { }
            public void LeaveAndGrade(Action<IReadOnlyList<ExamTaskGrade>> persist) { if (GradeFails) throw new IOException("genuine submission failure"); }
            public IReadOnlyList<ExamTaskGrade> GradeSaved(string id) => new ExamTaskGrade[0];
            public void Restart(string id) { }
            public void Close() { }
            public void Dispose() { }
        }
        [TestMethod]
        public async Task HostCreationFailureDoesNotHangCommandsOrDisposal()
        {
            using (var host = new MosWord2019.TestingStaHost(() => { throw new IOException("injected startup failure"); }, new AlwaysOnlineMonitor(), _ => { }))
                await Assert.ThrowsExceptionAsync<IOException>(() => host.InvokeAsync(e => true));
        }
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task HostWarningCodeComesFromCoordinatorFaultAndTransientTicksKeepRetrying(bool fault)
        {
            using (var folder = new TestWorkspace())
            {
                var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"));
                var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"), "en");
                var network = new AlwaysOnlineMonitor();
                var runtime = new EmptyRuntime { ObserveFailures = fault ? 0 : 1, GradeFails = fault };
                var warning = new TaskCompletionSource<MosWord2019.TestingWarningCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var host = new MosWord2019.TestingStaHost(() =>
                {
                    var clock = new StopwatchTestingClock();
                    return new TestingSessionCoordinator(prepared, new TestingSession(prepared.SessionId, prepared.Definition, clock, network), store, clock, runtime, () => { }, _ => { });
                }, network, _ => { }))
                {
                    host.Warning += value => warning.TrySetResult(value);
                    await host.StartAsync();
                    if (fault) await Assert.ThrowsExceptionAsync<IOException>(() => host.InvokeAsync(c => { c.Next(); return true; }));
                    Assert.AreEqual(warning.Task, await Task.WhenAny(warning.Task, Task.Delay(8000)), "No typed host warning.");
                    Assert.AreEqual(fault ? MosWord2019.TestingWarningCode.RecoveryRequired : MosWord2019.TestingWarningCode.ObservationDelayed, await warning.Task);
                    Assert.AreEqual(fault, await host.InvokeAsync(c => c.NeedsRecovery));
                    if (!fault)
                    {
                        Assert.AreEqual(runtime.Observed.Task, await Task.WhenAny(runtime.Observed.Task, Task.Delay(8000)), "Transient observation did not retry.");
                        Assert.AreEqual(TestingSessionStatus.Running, await host.InvokeAsync(c => c.Session.Status));
                        Assert.IsTrue(await host.InvokeAsync(c => c.Session.Elapsed > TimeSpan.Zero));
                    }
                }
            }
        }
        [TestMethod]
        public async Task RecoveryWarningIsNotSuppressedByEarlierTransientObservationWarning()
        {
            using (var folder = new TestWorkspace())
            {
                var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"));
                var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"), "en");
                var network = new AlwaysOnlineMonitor(); var clock = new TestingSessionTests.FakeClock();
                var runtime = new EmptyRuntime { ObserveFailures = 1 };
                var transient = new TaskCompletionSource<bool>(); var recovery = new TaskCompletionSource<bool>();
                using (var host = new MosWord2019.TestingStaHost(() => new TestingSessionCoordinator(prepared,
                    new TestingSession(prepared.SessionId, prepared.Definition, clock, network), store, clock, runtime, () => { }, _ => { }), network, _ => { }))
                {
                    host.Warning += code => { if (code == MosWord2019.TestingWarningCode.ObservationDelayed) transient.TrySetResult(true);
                        if (code == MosWord2019.TestingWarningCode.RecoveryRequired) recovery.TrySetResult(true); };
                    await host.StartAsync();
                    Assert.AreEqual(transient.Task, await Task.WhenAny(transient.Task, Task.Delay(8000)));
                    await host.InvokeAsync(c => { runtime.GradeFails = true; clock.Advance(3000); return true; });
                    Assert.AreEqual(recovery.Task, await Task.WhenAny(recovery.Task, Task.Delay(8000)), "A genuine fault must escape transient log suppression.");
                    Assert.IsTrue(await host.InvokeAsync(c => c.NeedsRecovery));
                }
            }
        }
    }
}
