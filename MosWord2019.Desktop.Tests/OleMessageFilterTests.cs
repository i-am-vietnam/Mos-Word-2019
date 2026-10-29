using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Services;
using MosWord2019.Word;

namespace MosWord2019.Tests
{
    [TestClass]
    public class OleMessageFilterTests
    {
        [TestMethod]
        public void ActualNativeOleMessageFilterRegistersAndRestoresOnSta()
        {
            RunSta(() => { using (new OleMessageFilter()) { } });
        }

        [DataTestMethod]
        [DataRow(0, 1, 250)]
        [DataRow(0, 2, 250)]
        [DataRow(9750, 1, 250)]
        [DataRow(9900, 2, 100)]
        [DataRow(9901, 1, -1)]
        [DataRow(9999, 1, -1)]
        [DataRow(9999, 2, -1)]
        [DataRow(10000, 1, -1)]
        [DataRow(10000, 2, -1)]
        [DataRow(10001, 2, -1)]
        [DataRow(int.MaxValue, 1, -1)]
        [DataRow(-1, 2, -1)]
        [DataRow(0, 0, -1)]
        [DataRow(0, 3, -1)]
        public void RetryPolicyHasBoundedMonotonicDeadline(int elapsed, int rejection, int expected)
        {
            Assert.AreEqual(expected, OleMessageFilter.RetryDelay(elapsed, rejection));
        }

        [TestMethod]
        public void RegistersAndRestoresPreviousFilterOnOwnerStaWithoutWord()
        {
            RunSta(() =>
            {
                var previous = new StubFilter();
                IOleMessageFilter current = previous;
                int registrations = 0;
                var filter = new OleMessageFilter((IOleMessageFilter next, out IOleMessageFilter old) =>
                {
                    registrations++;
                    old = current;
                    current = next;
                    return 0;
                });
                Assert.AreSame(filter, current);
                try { throw new InvalidOperationException("Simulated controller cleanup error"); }
                catch (InvalidOperationException) { }
                finally { filter.Dispose(); }
                filter.Dispose();
                Assert.AreSame(previous, current);
                Assert.AreEqual(2, registrations);
                Assert.AreEqual(0, filter.HandleInComingCall(0, IntPtr.Zero, 0, IntPtr.Zero));
                Assert.AreEqual(2, filter.MessagePending(IntPtr.Zero, 0, 0));
                Assert.AreEqual(-1, filter.RetryRejectedCall(IntPtr.Zero, 10000, 2));
            });
        }

        [TestMethod]
        public void RegistrationFailureIsReported()
        {
            RunSta(() => Assert.ThrowsException<COMException>(() => new OleMessageFilter(
                (IOleMessageFilter next, out IOleMessageFilter old) =>
                { old = null; return unchecked((int)0x80004005); })));
        }

        [TestMethod]
        public void RestoreFailureCanBeRetried()
        {
            RunSta(() =>
            {
                int calls = 0;
                var filter = new OleMessageFilter((IOleMessageFilter next, out IOleMessageFilter old) =>
                { old = null; return ++calls == 2 ? unchecked((int)0x80004005) : 0; });
                Assert.ThrowsException<COMException>(() => filter.Dispose());
                filter.Dispose();
                Assert.AreEqual(3, calls);
            });
        }

        [TestMethod]
        public void RegistrationRejectsMtaBeforeCallingRegistrar()
        {
            Exception failure = null;
            bool called = false;
            var thread = new Thread(() =>
            {
                try
                {
                    new OleMessageFilter((IOleMessageFilter next, out IOleMessageFilter old) =>
                    { called = true; old = null; return 0; });
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            Assert.IsTrue(thread.Join(5000));
            Assert.IsInstanceOfType(failure, typeof(InvalidOperationException));
            Assert.IsFalse(called);
        }

        [TestMethod]
        public void RestoreRejectsAnotherStaAndStillAllowsOwnerCleanup()
        {
            RunSta(() =>
            {
                int calls = 0;
                var filter = new OleMessageFilter((IOleMessageFilter next, out IOleMessageFilter old) =>
                { calls++; old = null; return 0; });
                RunSta(() => Assert.ThrowsException<InvalidOperationException>(() => filter.Dispose()));
                Assert.AreEqual(1, calls);
                filter.Dispose();
                Assert.AreEqual(2, calls);
            });
        }

        [TestMethod]
        public void ShortObservationBudgetsNestAndRestoreDefaultDeadline()
        {
            RunSta(() =>
            {
                using (var filter = new OleMessageFilter((IOleMessageFilter next, out IOleMessageFilter old) =>
                    { old = null; return 0; }))
                {
                    using (filter.BeginShortCallBudget(5000))
                    {
                        Assert.AreEqual(250, filter.RetryRejectedCall(IntPtr.Zero, 2000, 2));
                        using (filter.BeginShortCallBudget(250))
                        {
                            Assert.AreEqual(-1, filter.RetryRejectedCall(IntPtr.Zero, 249, 2));
                            Assert.AreEqual(-1, filter.RetryRejectedCall(IntPtr.Zero, 250, 2));
                        }
                        Assert.AreEqual(250, filter.RetryRejectedCall(IntPtr.Zero, 250, 2));
                        Assert.AreEqual(-1, filter.RetryRejectedCall(IntPtr.Zero, 5000, 2));
                    }
                    Assert.AreEqual(250, filter.RetryRejectedCall(IntPtr.Zero, 5000, 2));
                    Assert.AreEqual(-1, filter.RetryRejectedCall(IntPtr.Zero, 10000, 2));
                    Assert.ThrowsException<ArgumentOutOfRangeException>(() => filter.BeginShortCallBudget(0));
                }
            });
        }

        [DataTestMethod]
        [DataRow(unchecked((int)0x80010001))]
        [DataRow(unchecked((int)0x8001010A))]
        public void BusyDetectionTraversesNestedAggregateBranches(int errorCode)
        {
            var busy = new COMException("Word busy", errorCode);
            Assert.IsTrue(WordBusyException.IsBusy(busy));
            Assert.IsTrue(WordBusyException.IsBusy(new TargetInvocationException(new AggregateException(
                new InvalidOperationException("Nonbusy first branch"), new InvalidOperationException("Wrapped", busy)))));
            Assert.IsTrue(WordBusyException.IsBusy(new WordBusyException(busy)));
        }

        [DataTestMethod]
        [DataRow(unchecked((int)0x80010108))]
        [DataRow(unchecked((int)0x800706BA))]
        [DataRow(unchecked((int)0x80004005))]
        public void DisconnectionAndOtherFailuresAreNotBusy(int errorCode)
        {
            Assert.IsFalse(WordBusyException.IsBusy(new InvalidOperationException("Wrapped", new COMException("Other", errorCode))));
            Assert.IsFalse(WordBusyException.IsBusy(null));
        }

        [TestMethod]
        public void ControllerTranslatesRejectedComCallAndPreservesOtherFailures()
        {
            MethodInfo run = typeof(WordController).GetMethod("RunCom", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(Action) }, null);
            Assert.IsNotNull(run);
            var busy = new COMException("Rejected", unchecked((int)0x80010001));
            var wrapper = Assert.ThrowsException<TargetInvocationException>(() =>
                run.Invoke(null, new object[] { new Action(() => { throw new InvalidOperationException("Wrapped", busy); }) }));
            Assert.IsInstanceOfType(wrapper.InnerException, typeof(WordBusyException));
            var other = new COMException("Disconnected", unchecked((int)0x80010108));
            wrapper = Assert.ThrowsException<TargetInvocationException>(() =>
                run.Invoke(null, new object[] { new Action(() => { throw other; }) }));
            Assert.AreSame(other, wrapper.InnerException);
            var inputCallback = new COMException("Input synchronous callback", unchecked((int)0x8001010D));
            wrapper = Assert.ThrowsException<TargetInvocationException>(() =>
                run.Invoke(null, new object[] { new Action(() => { throw inputCallback; }) }));
            Assert.AreSame(inputCallback, wrapper.InnerException, "The UI must defer this call rather than reporting Word busy.");
        }

        [TestMethod]
        public void SynchronousInputCallbackStopsCleanupBeforeFurtherWordCalls()
        {
            MethodInfo guard = typeof(WordController).GetMethod("IsDeferredCleanup", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(guard);
            var callout = new COMException("Cannot call out during synchronous input", unchecked((int)0x8001010D));
            Assert.IsTrue((bool)guard.Invoke(null, new object[] { callout }));
            Assert.IsTrue((bool)guard.Invoke(null, new object[] { new AggregateException(
                new InvalidOperationException("Unrelated branch"), new InvalidOperationException("Wrapped", callout)) }));
            Assert.IsFalse((bool)guard.Invoke(null, new object[] { new COMException("Other", unchecked((int)0x80004005)) }));
        }

        [TestMethod]
        public void ControllerDisposeRestoresFilterAndCloseDoesNotUnregisterIt()
        {
            RunSta(() =>
            {
                int registrations = 0;
                var filter = new OleMessageFilter((IOleMessageFilter next, out IOleMessageFilter old) =>
                { registrations++; old = null; return 0; });
                var controller = new WordController();
                typeof(WordController).GetField("messageFilter", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, filter);
                controller.Close();
                Assert.AreEqual(1, registrations);
                controller.Dispose();
                controller.Dispose();
                Assert.AreEqual(2, registrations);
            });
        }

        [TestMethod]
        public void FailedCleanupRestoresFilterAndRetryRegistersAgainBeforeClosing()
        {
            RunSta(() =>
            {
                int registrations = 0;
                var controller = new WordController(() => new OleMessageFilter(
                    (IOleMessageFilter next, out IOleMessageFilter old) =>
                    { registrations++; old = null; return 0; }));
                // Pure state marker; neither Word nor any COM object is created.
                typeof(WordController).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, new WordSession());
                var failure = new InvalidOperationException("Simulated close failure");
                Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() =>
                    controller.DisposeOwnedState(() => { throw failure; })));
                Assert.AreEqual(2, registrations, "Failed cleanup must restore the first registration.");
                Assert.IsFalse((bool)typeof(WordController).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(controller));
                bool retried = false;
                controller.DisposeOwnedState(() =>
                {
                    Assert.AreEqual(3, registrations, "Retry must register before cleanup touches Word.");
                    retried = true;
                });
                Assert.IsTrue(retried);
                Assert.AreEqual(4, registrations);
                controller.DisposeOwnedState(() => Assert.Fail("Successful disposal must be idempotent."));
            });
        }

        [TestMethod]
        public void FilterRestoreFailureKeepsControllerRetryableAndRetainsBothErrors()
        {
            RunSta(() =>
            {
                int calls = 0;
                var filter = new OleMessageFilter((IOleMessageFilter next, out IOleMessageFilter old) =>
                { old = null; return ++calls == 2 ? unchecked((int)0x80004005) : 0; });
                var controller = new WordController();
                typeof(WordController).GetField("messageFilter", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, filter);
                var failure = new InvalidOperationException("Simulated close failure");
                var aggregate = Assert.ThrowsException<AggregateException>(() =>
                    controller.DisposeOwnedState(() => { throw failure; }));
                Assert.AreSame(failure, aggregate.InnerExceptions[0]);
                Assert.IsInstanceOfType(aggregate.InnerExceptions[1], typeof(COMException));
                Assert.IsFalse((bool)typeof(WordController).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(controller));
                controller.DisposeOwnedState(() => { });
                Assert.AreEqual(3, calls, "Retry must restore the still-registered filter.");
            });
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(5000), "Headless STA action timed out.");
            if (failure != null) throw new AssertFailedException("STA action failed: " + failure);
        }

        private sealed class StubFilter : IOleMessageFilter
        {
            public int HandleInComingCall(int a, IntPtr b, int c, IntPtr d) { return 0; }
            public int RetryRejectedCall(IntPtr a, int b, int c) { return -1; }
            public int MessagePending(IntPtr a, int b, int c) { return 2; }
        }
    }
}
