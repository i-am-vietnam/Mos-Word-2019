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
            public void Enter(string id, bool resume) { }
            public void AutosaveCurrent() { }
            public void Observe() { }
            public void SetInputEnabled(bool enabled) { }
            public void LeaveAndGrade(Action<IReadOnlyList<ExamTaskGrade>> persist) { }
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
    }
}
