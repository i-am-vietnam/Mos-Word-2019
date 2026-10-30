using System;
using System.Threading;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;

namespace MosWord2019.Projects
{
    public sealed class TestingHeartbeat : IDisposable
    {
        private readonly object gate = new object();
        private readonly TestingSession session;
        private readonly PreparedTestingSession prepared;
        private readonly TestingSessionStateStore store;
        private readonly ITestingClock clock;
        private readonly Timer timer;
        private TimeSpan lastWrite;
        private bool disposed, loggedFailure;
        public TestingHeartbeat(PreparedTestingSession prepared, TestingSession session, TestingSessionStateStore store, ITestingClock clock)
            : this(prepared, session, store, clock, true) { }
        internal TestingHeartbeat(PreparedTestingSession prepared, TestingSession session, TestingSessionStateStore store, ITestingClock clock, bool schedule)
        {
            this.prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            lastWrite = clock.Monotonic;
            if (schedule) timer = new Timer(_ => { try { Pulse(); } catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException))
                { lock (gate) { if (!loggedFailure) MosWord2019.Core.Diagnostics.AppLogger.Write("Testing heartbeat failed; retained prior state", ex); loggedFailure = true; } } }, null, 5000, 5000);
        }
        internal void Pulse()
        {
            lock (gate)
            {
                if (disposed || clock.Monotonic - lastWrite < TimeSpan.FromSeconds(5) ||
                    session.Status != TestingSessionStatus.Running && session.Status != TestingSessionStatus.Offline) return;
                store.SaveHeartbeat(prepared, session);
                lastWrite = clock.Monotonic; loggedFailure = false;
            }
        }
        public void Dispose() { lock (gate) { disposed = true; timer?.Dispose(); } }
    }
}
