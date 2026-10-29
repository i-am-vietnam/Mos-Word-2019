using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MosWord2019.Word
{
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);
        [PreserveSig] int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
    }

    /// <summary>Installed only on the controller's owner STA; OLE performs the retries on that STA.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class OleMessageFilter : IOleMessageFilter, IDisposable
    {
        internal delegate int RegisterFilter(IOleMessageFilter filter, out IOleMessageFilter previous);
        internal const int RetryWindowMilliseconds = 10000;
        private readonly int threadId;
        private readonly RegisterFilter register;
        private readonly IOleMessageFilter previous;
        private bool disposed;
        private BudgetScope budget;

        internal OleMessageFilter() : this(CoRegisterMessageFilter) { }

        // The injectable registrar keeps lifecycle tests independent of Office and COM activation.
        internal OleMessageFilter(RegisterFilter register)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            threadId = Thread.CurrentThread.ManagedThreadId;
            EnsureOwnerSta();
            this.register = register;
            IOleMessageFilter saved;
            Marshal.ThrowExceptionForHR(register(this, out saved));
            previous = saved;
        }

        internal static int RetryDelay(int elapsedMilliseconds, int rejectType)
        {
            // OLE supplies elapsed call time, so wall-clock adjustments cannot extend this deadline.
            if (rejectType != 1 && rejectType != 2) return -1;
            long remaining = RetryWindowMilliseconds - (long)unchecked((uint)elapsedMilliseconds);
            // OLE interprets 0..99 as an immediate retry, not a millisecond delay.
            return remaining >= 100 ? (int)Math.Min(250, remaining) : -1;
        }

        public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo) { return 0; }
        public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType)
        {
            int delay = RetryDelay(tickCount, rejectType);
            for (BudgetScope current = budget; current != null; current = current.Previous)
            {
                long remaining = current.LimitMilliseconds - Math.Max(current.ElapsedMilliseconds, unchecked((uint)tickCount));
                if (remaining < 100) return -1;
                if (delay > 0) delay = (int)Math.Min(delay, remaining);
            }
            return delay;
        }
        public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType) { return 2; }

        internal IDisposable BeginShortCallBudget(int milliseconds)
        {
            EnsureOwnerSta();
            if (disposed) throw new ObjectDisposedException(nameof(OleMessageFilter));
            if (milliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            budget = new BudgetScope(this, budget, milliseconds);
            return budget;
        }

        public void Dispose()
        {
            EnsureOwnerSta();
            if (disposed) return;
            IOleMessageFilter replaced;
            Marshal.ThrowExceptionForHR(register(previous, out replaced));
            disposed = true;
        }

        private void EnsureOwnerSta()
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId ||
                Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("The OLE message filter must remain on its creating STA thread.");
        }

        private sealed class BudgetScope : IDisposable
        {
            private readonly OleMessageFilter owner;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            internal readonly BudgetScope Previous;
            internal readonly int LimitMilliseconds;
            private bool disposed;
            internal long ElapsedMilliseconds { get { return clock.ElapsedMilliseconds; } }

            internal BudgetScope(OleMessageFilter owner, BudgetScope previous, int limit)
            { this.owner = owner; Previous = previous; LimitMilliseconds = limit; }

            public void Dispose()
            {
                owner.EnsureOwnerSta();
                if (disposed) return;
                if (!ReferenceEquals(owner.budget, this))
                    throw new InvalidOperationException("Dispose Word call budgets in reverse order.");
                owner.budget = Previous;
                disposed = true;
            }
        }

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter filter, out IOleMessageFilter previous);
    }
}
