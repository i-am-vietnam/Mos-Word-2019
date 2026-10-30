using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Word;

namespace MosWord2019.Desktop.Tests
{
    [TestClass]
    public sealed class OwnedWordRecoveryTests
    {
        [DataTestMethod]
        [DataRow("match", true)]
        [DataRow("pid", false)]
        [DataRow("created", false)]
        [DataRow("image", false)]
        [DataRow("changed", false)]
        [DataRow("absent", false)]
        [DataRow("machine", false)]
        public void RecoveryTerminatesOnlyReverifiedIdentityOnTheSameHandle(string variant, bool expectedStop)
        {
            var expected = new OwnedWordProcessIdentity(123, 100000, @"C:\Office\WINWORD.EXE");
            var wrong = new OwnedWordProcessIdentity(variant == "pid" ? 124 : 123, variant == "created" ? 100001 : 100000,
                variant == "image" ? @"C:\Office\EXCEL.EXE" : @"C:\Other\WINWORD.EXE");
            var fake = new Fake { First = variant == "match" || variant == "changed" ? expected : wrong,
                Second = variant == "changed" ? wrong : expected };
            int opens = 0;
            var recovery = new OwnedWordProcessRecovery(pid => { opens++; Assert.AreEqual(123, pid); return variant == "absent" ? null : fake; });
            Assert.AreEqual(expectedStop, recovery.Stop(expected, variant == "machine" ? "different-machine" : Environment.MachineName));
            Assert.AreEqual(expectedStop, fake.Terminated);
            if (expectedStop) Assert.AreEqual(2, fake.Reads);
            if (variant == "machine") Assert.AreEqual(0, opens);
            else if (variant != "absent") Assert.IsTrue(fake.Disposed);
        }
        [DataTestMethod]
        [DataRow(31)]
        [DataRow(5)]
        [DataRow(87)]
        public void UnreadableProcessIdentityNeverBlocksRecoveryOrTerminates(int error)
        {
            var expected = new OwnedWordProcessIdentity(17120, 100000, @"C:\Office\WINWORD.EXE");
            var fake = new Fake { IdentityError = error };
            var recovery = new OwnedWordProcessRecovery(_ => fake, _ => { });
            Assert.IsFalse(recovery.IsRunning(expected, Environment.MachineName));
            Assert.IsFalse(recovery.Stop(expected, Environment.MachineName));
            Assert.IsFalse(fake.Terminated);
        }
        [TestMethod]
        public void ExitedProcessIsCheckedBeforeReadingItsIdentity()
        {
            var expected = new OwnedWordProcessIdentity(17120, 100000, @"C:\Office\WINWORD.EXE");
            var fake = new Fake { HasExited = true, IdentityError = 31 };
            var recovery = new OwnedWordProcessRecovery(_ => fake, _ => { });
            Assert.IsFalse(recovery.IsRunning(expected, Environment.MachineName));
            Assert.IsFalse(recovery.Stop(expected, Environment.MachineName));
            Assert.AreEqual(0, fake.Reads); Assert.IsFalse(fake.Terminated);
        }
        [DataTestMethod]
        [DataRow(31)]
        [DataRow(5)]
        public void OpeningUnqueryableProcessRefusesOwnershipAndLogsOncePerOperation(int error)
        {
            var expected = new OwnedWordProcessIdentity(17120, 100000, @"C:\Office\WINWORD.EXE");
            int logs = 0;
            var recovery = new OwnedWordProcessRecovery(_ => { throw new Win32Exception(error); }, _ => logs++);
            Assert.IsFalse(recovery.IsRunning(expected, Environment.MachineName)); Assert.AreEqual(1, logs);
            Assert.IsFalse(recovery.Stop(expected, Environment.MachineName)); Assert.AreEqual(2, logs);
        }
        [TestMethod]
        public void RetainedHandleOfExitedProcessReturnsNoIdentity()
        {
            // A short-lived test-owned cmd process exercises the native exit probe without Word.
            using (var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }))
            using (var handle = new SafeProcessHandle(child.Handle, false))
            using (var retained = new WinApiProcessHelper.OwnedProcess(child.Id, handle))
            {
                Assert.IsTrue(child.WaitForExit(5000));
                Assert.IsTrue(retained.HasExited); Assert.IsNull(retained.ReadIdentity());
            }
        }
        [TestMethod]
        public void TimedOutRetiredApplicationCannotBeTerminatedByCloseOrForceRecovery()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    // This test-owned process outlives the retirement wait; it is never a termination target.
                    using (var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 13 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true }))
                    using (var handle = new SafeProcessHandle(child.Handle, false))
                    using (var retained = new WinApiProcessHelper.OwnedProcess(child.Id, handle))
                    using (var controller = new WordController())
                    {
                        typeof(WordController).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)
                            .SetValue(controller, new WordSession { Process = retained });
                        try
                        {
                            var elapsed = Stopwatch.StartNew();
                            Assert.ThrowsException<TimeoutException>(() => controller.ReleaseClosedApplication());
                            Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromSeconds(9));
                            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(12));
                            Assert.IsNull(controller.OwnedProcessIdentity, "Retired applications cannot be live force-close targets.");
                            controller.Close(); Assert.IsFalse(child.HasExited, "Closing the trainer must preserve a retired application that did not exit.");
                        }
                        finally { Assert.IsTrue(child.WaitForExit(6000), "Test-owned child must exit naturally."); }
                    }
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private sealed class Fake : IRecoveredWordProcess
        {
            internal OwnedWordProcessIdentity First, Second;
            internal int Reads; internal bool Terminated, Disposed;
            internal int IdentityError;
            public bool HasExited { get; set; }
            public OwnedWordProcessIdentity ReadIdentity()
            { Reads++; if (IdentityError != 0) throw new Win32Exception(IdentityError); return Reads == 1 ? First : Second; }
            public void Terminate() { Assert.AreEqual(2, Reads); Terminated = true; }
            public void Dispose() { Disposed = true; }
        }
    }
}
