using MosWord2019.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MosWord2019.Projects.OnlineSessions
{
    /// <summary>Server authorization projected using monotonic elapsed time; no client wall clock is trusted.</summary>
    public sealed class SessionConnectivityMonitor : IConnectivityMonitor, IDisposable
    {
        private readonly object sync = new object();
        private readonly Queue<Action> notifications = new Queue<Action>();
        private int drainingNotifications;
        private readonly Guid sessionId;
        private readonly string mode;
        private readonly IHeartbeatTransport transport;
        private readonly ITestingClock clock;
        private readonly Func<TimeSpan?> elapsedProvider;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Timer timer;
        private bool disposed, inFlight, online, rejected, hasReply, observedOffline;
        private int failures;
        private string token;
        private TimeSpan anchor, projectionAnchor, lastSuccess, lastObserved, nextPoll;
        private DateTimeOffset serverTime, expiresAt;
        private TimeSpan authorizedRemaining;
        public event Action<bool> ConnectivityChanged;
        public event Action<string> SessionTokenRefreshed;
        public event Action<HeartbeatSessionRejectedException> SessionRejected;

        public SessionConnectivityMonitor(Guid sessionId, string mode, string token, IHeartbeatTransport transport, ITestingClock clock, Func<TimeSpan?> elapsedProvider = null)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session is required.", nameof(sessionId));
            if (mode != "Training" && mode != "Testing") throw new ArgumentException("A selected session mode is required.", nameof(mode));
            if (!ValidToken(token)) throw new ArgumentException("A session token is required.", nameof(token));
            this.sessionId = sessionId; this.mode = mode; this.token = token;
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.elapsedProvider = elapsedProvider;
            lastObserved = clock.Monotonic;
        }
        public string SessionToken { get { lock (sync) return token; } }
        public bool IsOnline { get { return ReadEvaluated(() => !disposed && online); } }
        public bool IsSessionRejected { get { lock (sync) return rejected; } }
        public bool IsAuthorizationExpired { get { return ReadEvaluated(() => rejected || (hasReply && Remaining() == TimeSpan.Zero)); } }
        public DateTimeOffset? ProjectedServerTime { get { return ReadEvaluated(() => hasReply ? serverTime + PositiveElapsed(projectionAnchor) : (DateTimeOffset?)null); } }
        public TimeSpan? TrainingRemaining { get { return ReadEvaluated(() => mode == "Training" && hasReply ? Remaining() : (TimeSpan?)null); } }
        // This is the server hard cap, separate from the existing 50-minute active exam timer.
        public TimeSpan? TestingRemaining { get { return ReadEvaluated(() => mode == "Testing" && hasReply ? Remaining() : (TimeSpan?)null); } }

        private T ReadEvaluated<T>(Func<T> read)
        {
            T value;
            lock (sync) { Evaluate(); value = read(); }
            DrainNotifications();
            return value;
        }

        public void Start()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(SessionConnectivityMonitor));
                if (timer == null) timer = new Timer(Tick, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            }
        }
        private void Tick(object state)
        {
            bool poll;
            lock (sync) { Evaluate(); poll = !disposed && !rejected && !inFlight && clock.Monotonic >= nextPoll; }
            DrainNotifications();
            // Poll catches transport failures. The timer also evaluates the watchdog while a request hangs.
            if (poll) _ = PollAsync();
        }
        public async Task PollAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                string sentToken; bool wasOffline; TimeSpan sentAt;
                lock (sync)
                {
                    Evaluate();
                    if (disposed || rejected || inFlight || cancellationToken.IsCancellationRequested) return;
                    inFlight = true; sentToken = token; wasOffline = hasReply && observedOffline;
                    sentAt = clock.Monotonic; nextPoll = sentAt + TimeSpan.FromSeconds(30);
                }
                DrainNotifications();
                Task<HeartbeatReply> send = null;
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken))
                {
                    try
                    {
                        long? elapsed = null;
                        if (mode == "Testing" && elapsedProvider != null)
                        {
                            TimeSpan? value = elapsedProvider();
                            if (value.HasValue) { if (value.Value < TimeSpan.Zero || value.Value > TimeSpan.FromHours(24)) throw new InvalidDataException("Invalid client elapsed time."); elapsed = value.Value.Ticks / TimeSpan.TicksPerMillisecond; }
                        }
                        send = transport.SendAsync(sessionId, sentToken, elapsed, wasOffline, linked.Token);
                        var timeout = Task.Delay(TimeSpan.FromSeconds(10), linked.Token);
                        if (await Task.WhenAny(send, timeout).ConfigureAwait(false) != send)
                        {
                            linked.Cancel(); cancellationToken.ThrowIfCancellationRequested();
                            throw new TimeoutException("Heartbeat timed out.");
                        }
                        HeartbeatReply reply = await send.ConfigureAwait(false);
                        lock (sync)
                        {
                            if (disposed || rejected || linked.IsCancellationRequested) return;
                            Evaluate(); if (rejected) return;
                            Validate(reply, sentAt);
                            TimeSpan receivedAt = clock.Monotonic;
                            TimeSpan requestDuration = receivedAt - sentAt;
                            TimeSpan replyRemaining = TimeSpan.FromSeconds(reply.RemainingSeconds);
                            // Small clock-rate differences must never replenish authorization already consumed.
                            if (hasReply && replyRemaining > Remaining() + requestDuration) replyRemaining = Remaining() + requestDuration;
                            anchor = sentAt; serverTime = reply.ServerTime; expiresAt = reply.ExpiresAt;
                            projectionAnchor = clock.Monotonic;
                            authorizedRemaining = replyRemaining;
                            lastSuccess = clock.Monotonic; hasReply = true; failures = 0; observedOffline = false;
                            token = reply.SessionToken;
                            SetOnline(Remaining() > TimeSpan.Zero);
                            Action<string> refreshed = SessionTokenRefreshed;
                            string refreshedToken = token;
                            if (!disposed && refreshed != null) notifications.Enqueue(() => refreshed(refreshedToken));
                        }
                    }
                    catch (HeartbeatSessionRejectedException ex) { lock (sync) { if (!disposed && !cancellationToken.IsCancellationRequested) Reject(ex); } }
                    catch (InvalidDataException) { lock (sync) { if (!disposed && !cancellationToken.IsCancellationRequested) Reject(new HeartbeatSessionRejectedException(0)); } }
                    catch (Exception) { lock (sync) { if (!disposed && !cancellationToken.IsCancellationRequested && !rejected) { failures++; Evaluate(); if (failures >= 3) SetOnline(false); } } }
                    finally
                    {
                        linked.Cancel();
                        if (send == null || send.IsCompleted) { lock (sync) inFlight = false; }
                        else _ = send.ContinueWith(t => { var ignored = t.Exception; lock (sync) inFlight = false; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            }
            finally { DrainNotifications(); }
        }
        private void Validate(HeartbeatReply r, TimeSpan sentAt)
        {
            double cap = mode == "Training" ? 14400 : 7200;
            if (r == null || r.SessionId != sessionId || r.Mode != mode || r.ServerTime.Offset != TimeSpan.Zero || r.ExpiresAt.Offset != TimeSpan.Zero ||
                r.ExpiresAt <= r.ServerTime || (r.ExpiresAt - r.ServerTime).TotalSeconds > cap ||
                double.IsNaN(r.RemainingSeconds) || double.IsInfinity(r.RemainingSeconds) || r.RemainingSeconds < 0 ||
                r.RemainingSeconds > (r.ExpiresAt - r.ServerTime).TotalSeconds || !ValidToken(r.SessionToken) ||
                (hasReply && (r.ServerTime < serverTime ||
                    r.ServerTime + TimeSpan.FromSeconds(2) < serverTime + (sentAt > projectionAnchor ? sentAt - projectionAnchor : TimeSpan.Zero) ||
                    r.ExpiresAt > expiresAt)))
                throw new InvalidDataException("Invalid server heartbeat authorization.");
        }
        private static bool ValidToken(string value) { return !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Any(char.IsControl); }
        private TimeSpan PositiveElapsed(TimeSpan since) { TimeSpan value = clock.Monotonic - since; return value < TimeSpan.Zero ? TimeSpan.Zero : value; }
        private TimeSpan Elapsed() { return PositiveElapsed(anchor); }
        private TimeSpan Remaining() { if (rejected) return TimeSpan.Zero; TimeSpan value = authorizedRemaining - Elapsed(); return value > TimeSpan.Zero ? value : TimeSpan.Zero; }
        private void Evaluate()
        {
            if (disposed || rejected) return;
            TimeSpan now = clock.Monotonic;
            if (now < lastObserved) { Reject(new HeartbeatSessionRejectedException(0)); return; }
            lastObserved = now;
            if (hasReply && (now - lastSuccess >= TimeSpan.FromSeconds(90) || Remaining() == TimeSpan.Zero)) SetOnline(false);
        }
        private void SetOnline(bool value)
        {
            if (online == value) return;
            online = value; if (!value && hasReply) observedOffline = true;
            Action<bool> changed = ConnectivityChanged;
            if (!disposed && changed != null) notifications.Enqueue(() => changed(value));
        }
        private void Reject(HeartbeatSessionRejectedException reason)
        {
            if (rejected || disposed) return;
            rejected = true; SetOnline(false);
            Action<HeartbeatSessionRejectedException> handler = SessionRejected;
            if (!disposed && handler != null) notifications.Enqueue(() => handler(reason));
        }
        private void DrainNotifications()
        {
            // Never wait for another drainer: its consumer may hold a lock needed by this caller.
            if (Interlocked.CompareExchange(ref drainingNotifications, 1, 0) != 0) return;
            while (true)
            {
                Action notification;
                lock (sync)
                {
                    if (disposed) notifications.Clear();
                    if (notifications.Count == 0) { Interlocked.Exchange(ref drainingNotifications, 0); return; }
                    notification = notifications.Dequeue();
                }
                try { notification(); }
                catch (Exception) { /* Consumer failures must not stop heartbeat monitoring or subsequent notifications. */ }
            }
        }
        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; online = false; notifications.Clear(); timer?.Dispose(); }
            lifetime.Cancel();
        }
    }
}
