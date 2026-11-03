using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Projects.OnlineSessions;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class OnlineSessionTests
    {
        [DataTestMethod]
        [DataRow("success")]
        [DataRow("getter")]
        [DataRow("rejection")]
        public async Task NotificationsNeverHoldMonitorLockWhileCallingConsumers(string path)
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                if (path == "getter") await m.PollAsync();
                bool blocked = false; int notifications = 0;
                Action callback = () =>
                {
                    notifications++;
                    Task read = Task.Run(() => { string token = m.SessionToken; bool online = m.IsOnline; });
                    if (!read.Wait(300)) blocked = true;
                };
                m.ConnectivityChanged += _ => callback();
                m.SessionTokenRefreshed += _ => callback();
                m.SessionRejected += _ => callback();
                if (path == "getter") { f.Clock.Advance(90); Assert.IsFalse(m.IsOnline); }
                else { if (path == "rejection") f.Transport.Reject = 401; await m.PollAsync(); }
                Assert.IsTrue(notifications > 0); Assert.IsFalse(blocked, "Consumer could not read the monitor from another thread during notification.");
            }
        }

        [TestMethod]
        public async Task RealTestingEngineCanStartFromNotificationWithoutLockInversionAndStillPauses()
        {
            var f = new Fixture(); f.Mode = "Testing";
            var engineClock = new TestingSessionTests.FakeClock();
            using (var m = f.Monitor())
            using (var engine = new MosWord2019.Core.Models.TestingSession(f.Id.ToString("N"), TestingSessionTests.Definition(), engineClock, m))
            {
                bool first = true, blocked = false;
                m.ConnectivityChanged += online =>
                {
                    if (!online || !first) return;
                    first = false;
                    Task start = Task.Run(() => engine.Start());
                    if (!start.Wait(500)) blocked = true;
                };
                await m.PollAsync(); Assert.IsFalse(blocked, "Testing.Start could not read monitor while consumer callback ran.");
                Assert.AreEqual(MosWord2019.Core.Models.TestingSessionStatus.Running, engine.Status);
                f.Clock.Advance(90); engineClock.Advance(90); Assert.IsFalse(m.IsOnline);
                Assert.IsTrue(engine.OverlayRequested); Assert.AreEqual(TimeSpan.FromSeconds(90), engine.Elapsed);
                f.Clock.Advance(30); engineClock.Advance(30); await m.PollAsync();
                Assert.IsFalse(engine.OverlayRequested); Assert.AreEqual(TimeSpan.FromSeconds(90), engine.Elapsed);
            }
        }

        [TestMethod]
        public async Task ReentrantConcurrentPollPreservesCapturedTokenOrderWithoutWaitingForDrainer()
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                int sequence = 0; bool blocked = false;
                var tokens = new System.Collections.Generic.List<string>();
                f.Transport.Transform = r => { r.SessionToken = "token" + (++sequence); return r; };
                m.ConnectivityChanged += online =>
                {
                    if (online && !Task.Run(() => m.PollAsync()).Wait(500)) blocked = true;
                };
                m.SessionTokenRefreshed += tokens.Add;
                await m.PollAsync();
                Assert.IsFalse(blocked); CollectionAssert.AreEqual(new[] { "token1", "token2" }, tokens);
            }
        }

        [TestMethod]
        public async Task DisposalFromConnectivityCallbackClearsQueuedTokenNotification()
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                int refreshed = 0;
                m.ConnectivityChanged += _ => m.Dispose();
                m.SessionTokenRefreshed += _ => refreshed++;
                await m.PollAsync(); Assert.AreEqual(0, refreshed); Assert.IsFalse(m.IsOnline);
            }
        }

        [TestMethod]
        public async Task ThreeFailuresGoOfflineAndSuccessfulReplyRecovers()
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                Assert.IsFalse(m.IsOnline); await m.PollAsync(); Assert.IsTrue(m.IsOnline);
                f.Transport.Fail = true;
                await m.PollAsync(); await m.PollAsync(); Assert.IsTrue(m.IsOnline);
                await m.PollAsync(); Assert.IsFalse(m.IsOnline);
                f.Transport.Fail = false; await m.PollAsync(); Assert.IsTrue(m.IsOnline);
                Assert.IsTrue(f.Transport.WasOffline);
            }
        }

        [TestMethod]
        public async Task InitialContactDoesNotClaimCrashAsNetworkAndTrainingNeverReadsExamElapsed()
        {
            var f = new Fixture(); using (var m = f.Monitor(() => { throw new AssertFailedException("Training must not ask for active exam time."); }))
            {
                await m.PollAsync(); Assert.IsFalse(f.Transport.WasOffline); Assert.IsNull(f.Transport.Elapsed);
                f.Clock.Advance(90); Assert.IsFalse(m.IsOnline);
                await m.PollAsync(); Assert.IsTrue(f.Transport.WasOffline);
                await m.PollAsync(); Assert.IsFalse(f.Transport.WasOffline);
            }
        }

        [TestMethod]
        public async Task CallerCancellationDiscardsLateReplyAndMonotonicRegressionLocks()
        {
            var f = new Fixture(); using (var m = f.Monitor())
            using (var cancellation = new CancellationTokenSource())
            {
                await m.PollAsync(); var pending = new TaskCompletionSource<HeartbeatReply>(); f.Transport.Pending = pending.Task;
                int events = 0; m.SessionTokenRefreshed += _ => events++;
                Task poll = m.PollAsync(cancellation.Token); cancellation.Cancel();
                var late = f.Reply(); late.SessionToken = "late"; pending.SetResult(late); await poll;
                Assert.AreEqual("renewed", m.SessionToken); Assert.AreEqual(0, events);
                f.Clock.Advance(10); Assert.IsTrue(m.IsOnline); f.Clock.Advance(-1);
                Assert.IsFalse(m.IsOnline); Assert.IsTrue(m.IsSessionRejected);
            }
        }

        [TestMethod]
        public async Task StartSchedulesThirtySecondHeartbeatsAndWatchdogRunsDuringHungRequest()
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                var first = new TaskCompletionSource<bool>(); var offline = new TaskCompletionSource<bool>();
                m.SessionTokenRefreshed += _ => first.TrySetResult(true);
                m.ConnectivityChanged += value => { if (!value) offline.TrySetResult(true); };
                m.Start(); Assert.AreSame(first.Task, await Task.WhenAny(first.Task, Task.Delay(4000)));
                f.Clock.Advance(29); await Task.Delay(1100); Assert.AreEqual(1, f.Transport.Calls);
                var pending = new TaskCompletionSource<HeartbeatReply>(); f.Transport.Pending = pending.Task;
                f.Clock.Advance(1); await Task.Delay(1100); Assert.AreEqual(2, f.Transport.Calls);
                f.Clock.Advance(60);
                Assert.AreSame(offline.Task, await Task.WhenAny(offline.Task, Task.Delay(4000)));
                Assert.AreEqual(2, f.Transport.Calls); m.Dispose(); pending.SetResult(f.Reply());
            }
        }

        [TestMethod]
        public async Task RequestTimeoutIsBoundedAndRetainsSingleFlightUntilUncooperativeTransportFinishes()
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                await m.PollAsync(); var pending = new TaskCompletionSource<HeartbeatReply>(); f.Transport.Pending = pending.Task;
                Task poll = m.PollAsync(); Assert.AreSame(poll, await Task.WhenAny(poll, Task.Delay(13000)));
                await poll; await m.PollAsync(); Assert.AreEqual(2, f.Transport.Calls);
                pending.SetResult(f.Reply());
            }
        }

        [TestMethod]
        public async Task NinetySecondsInclusiveAndTrainingDeadlineIgnoreWallClockAndOffline()
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                await m.PollAsync(); f.Clock.Advance(89.999); Assert.IsTrue(m.IsOnline);
                f.Clock.Advance(.001); Assert.IsFalse(m.IsOnline);
                Assert.AreEqual(TimeSpan.FromSeconds(14310), m.TrainingRemaining);
                Assert.AreEqual(f.Server.AddSeconds(90), m.ProjectedServerTime);
                f.Clock.Advance(14310); Assert.AreEqual(TimeSpan.Zero, m.TrainingRemaining);
                Assert.IsTrue(m.IsAuthorizationExpired);
            }
        }

        [TestMethod]
        public async Task HungRequestCannotOverlapOrPublishAfterDispose()
        {
            var f = new Fixture(); var pending = new TaskCompletionSource<HeartbeatReply>();
            var m = f.Monitor(); await m.PollAsync();
            int events = 0; m.SessionTokenRefreshed += _ => events++;
            f.Transport.Pending = pending.Task; Task poll = m.PollAsync(); await m.PollAsync();
            Assert.AreEqual(2, f.Transport.Calls); f.Clock.Advance(90); Assert.IsFalse(m.IsOnline);
            m.Dispose(); pending.SetResult(f.Reply()); await poll;
            Assert.AreEqual(0, events); Assert.AreEqual("renewed", m.SessionToken);
        }

        [TestMethod]
        public async Task TestingElapsedAndTokenRefreshUseValidatedPacketOnly()
        {
            var f = new Fixture(); f.Mode = "Testing";
            using (var m = f.Monitor(() => TimeSpan.FromSeconds(12)))
            {
                string token = null; m.SessionTokenRefreshed += t => token = t;
                await m.PollAsync(); Assert.AreEqual(12000L, f.Transport.Elapsed);
                Assert.AreEqual("renewed", token); Assert.AreEqual(TimeSpan.FromHours(2), m.TestingRemaining);
                Assert.IsNull(m.TrainingRemaining);
            }
        }

        [TestMethod]
        public async Task SmallServerClockSlewStaysOnlineWithoutReplenishingAuthorization()
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                await m.PollAsync();
                f.Transport.Transform = r =>
                {
                    double driftSeconds = f.Clock.Monotonic.TotalSeconds / 30000;
                    r.ServerTime = r.ServerTime.AddSeconds(-driftSeconds);
                    r.RemainingSeconds = (r.ExpiresAt - r.ServerTime).TotalSeconds;
                    return r;
                };
                f.Clock.Advance(30); await m.PollAsync();
                Assert.IsTrue(m.IsOnline); Assert.IsFalse(m.IsSessionRejected);
                Assert.AreEqual(TimeSpan.FromSeconds(14370), m.TrainingRemaining);
                f.Clock.Advance(30); await m.PollAsync();
                Assert.IsTrue(m.IsOnline); Assert.AreEqual(TimeSpan.FromSeconds(14340), m.TrainingRemaining);
            }
        }

        [DataTestMethod]
        [DataRow("Training", 143000000001L, 14300.0000001)]
        [DataRow("Testing", 71000000001L, 7100.0000001)]
        public async Task ModernServerSecondsRoundingDoesNotRejectFrameworkClient(string mode, long ticks, double seconds)
        {
            var f = new Fixture(); f.Mode = mode;
            using (var m = f.Monitor())
            {
                f.Transport.Transform = r =>
                {
                    r.ExpiresAt = r.ServerTime.AddTicks(ticks);
                    // Literal values produced by .NET 10 TimeSpan.TotalSeconds.
                    r.RemainingSeconds = seconds;
                    return r;
                };
                await m.PollAsync();
                Assert.IsTrue(m.IsOnline);
                Assert.IsFalse(m.IsSessionRejected);
                Assert.AreEqual("renewed", m.SessionToken);
            }
        }

        [DataTestMethod]
        [DataRow("Training")]
        [DataRow("Testing")]
        public async Task RemainingSecondsOverclaimBeyondOneTickRemainsTerminal(string mode)
        {
            var f = new Fixture(); f.Mode = mode;
            using (var m = f.Monitor())
            {
                f.Transport.Transform = r =>
                {
                    r.RemainingSeconds += 0.000001;
                    r.SessionToken = "untrusted";
                    return r;
                };
                await m.PollAsync();
                Assert.IsTrue(m.IsSessionRejected);
                Assert.IsFalse(m.IsOnline);
                Assert.AreEqual("initial", m.SessionToken);
            }
        }

        [DataTestMethod]
        [DataRow("projection")]
        [DataRow("deadline")]
        public async Task MeaningfulServerClockRegressionOrDeadlineExtensionRemainsTerminal(string invalid)
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                await m.PollAsync(); f.Clock.Advance(30);
                f.Transport.Transform = r =>
                {
                    if (invalid == "projection") r.ServerTime = r.ServerTime.AddSeconds(-3);
                    if (invalid == "deadline") r.ExpiresAt = r.ExpiresAt.AddSeconds(1);
                    r.RemainingSeconds = (r.ExpiresAt - r.ServerTime).TotalSeconds;
                    return r;
                };
                await m.PollAsync(); Assert.IsFalse(m.IsOnline); Assert.IsTrue(m.IsSessionRejected);
            }
        }

        [DataTestMethod]
        [DataRow(-1L)]
        [DataRow(86400001L)]
        public async Task InvalidClientElapsedNeverLeavesMonitor(long milliseconds)
        {
            var f = new Fixture(); f.Mode = "Testing";
            using (var m = f.Monitor(() => TimeSpan.FromMilliseconds(milliseconds)))
            {
                await m.PollAsync(); Assert.AreEqual(0, f.Transport.Calls);
                Assert.IsFalse(m.IsOnline); Assert.IsTrue(m.IsSessionRejected);
            }
        }

        [DataTestMethod]
        [DataRow("id")]
        [DataRow("mode")]
        [DataRow("utc")]
        [DataRow("deadline")]
        [DataRow("remaining")]
        [DataRow("regression")]
        public async Task InvalidPacketNeverAuthorizesOrRefreshesToken(string invalid)
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                if (invalid == "regression") { await m.PollAsync(); f.Server = f.Server.AddSeconds(-1); }
                f.Transport.Transform = r =>
                {
                    if (invalid == "id") r.SessionId = Guid.NewGuid();
                    if (invalid == "mode") r.Mode = "Testing";
                    if (invalid == "utc") r.ServerTime = r.ServerTime.ToOffset(TimeSpan.FromHours(1));
                    if (invalid == "deadline") r.ExpiresAt = r.ServerTime.AddHours(5);
                    if (invalid == "remaining") r.RemainingSeconds++;
                    r.SessionToken = "untrusted"; return r;
                };
                await m.PollAsync(); Assert.IsFalse(m.IsOnline); Assert.AreNotEqual("untrusted", m.SessionToken);
            }
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(410)]
        public async Task AuthorizationRejectionIsTerminal(int status)
        {
            var f = new Fixture(); using (var m = f.Monitor())
            {
                int rejected = 0; m.SessionRejected += _ => rejected++;
                f.Transport.Reject = status; await m.PollAsync(); f.Transport.Reject = 0; await m.PollAsync();
                Assert.AreEqual(1, rejected); Assert.AreEqual(1, f.Transport.Calls); Assert.IsFalse(m.IsOnline);
            }
        }

        [TestMethod]
        public async Task HttpTransportUsesExactRouteBearerAndNullableBody()
        {
            var f = new Fixture(); var handler = new Handler(f);
            using (var client = new HttpClient(handler))
            using (var transport = new HttpHeartbeatTransport(new Uri("https://example.invalid/"), client))
            {
                await transport.SendAsync(f.Id, "secret", null, true, CancellationToken.None);
                Assert.AreEqual("/api/sessions/" + f.Id.ToString("D") + "/heartbeat", handler.Path);
                Assert.AreEqual("Bearer secret", handler.Auth); Assert.AreEqual("POST", handler.Method);
                Assert.AreEqual(JTokenType.Null, handler.Body["clientElapsedMilliseconds"].Type);
                Assert.IsTrue((bool)handler.Body["wasOffline"]);
                await transport.SendAsync(f.Id, "secret", 12345, false, CancellationToken.None);
                Assert.AreEqual(12345L, (long)handler.Body["clientElapsedMilliseconds"]);
                Assert.IsFalse((bool)handler.Body["wasOffline"]);
            }
        }

        [DataTestMethod]
        [DataRow(401)]
        [DataRow(410)]
        public async Task HttpAuthorizationStatusRemainsTerminal(int status)
        {
            var f = new Fixture(); var handler = new Handler(f) { Status = (HttpStatusCode)status };
            using (var client = new HttpClient(handler))
            using (var transport = new HttpHeartbeatTransport(new Uri("https://example.invalid/"), client))
            {
                var rejected = await Assert.ThrowsExceptionAsync<HeartbeatSessionRejectedException>(() => transport.SendAsync(f.Id, "secret", null, false, CancellationToken.None));
                Assert.AreEqual(status, rejected.StatusCode);
            }
        }

        [DataTestMethod]
        [DataRow("offset")]
        [DataRow("missing")]
        [DataRow("duplicate")]
        [DataRow("redirect")]
        public async Task HttpMalformedOrRedirectedPacketCannotAuthorize(string invalid)
        {
            var f = new Fixture(); var handler = new Handler(f);
            if (invalid == "offset") { var r = f.Reply(); r.ServerTime = r.ServerTime.ToOffset(TimeSpan.FromHours(1)); handler.Text = Newtonsoft.Json.JsonConvert.SerializeObject(r); }
            if (invalid == "missing") handler.Text = "{}";
            if (invalid == "duplicate") handler.Text = "{\"mode\":\"Training\",\"mode\":\"Testing\"}";
            if (invalid == "redirect") handler.Redirect = true;
            using (var client = new HttpClient(handler))
            using (var transport = new HttpHeartbeatTransport(new Uri("https://example.invalid/"), client))
            using (var m = new SessionConnectivityMonitor(f.Id, "Training", "secret", transport, f.Clock))
            {
                await m.PollAsync(); Assert.IsFalse(m.IsOnline); Assert.IsTrue(m.IsSessionRejected);
                Assert.AreEqual("secret", m.SessionToken);
            }
        }

        [DataTestMethod]
        [DataRow("http://example.invalid/")]
        [DataRow("https://example.invalid/path")]
        [DataRow("https://user@example.invalid/")]
        [DataRow("https://example.invalid/?query")]
        [DataRow("https://example.invalid/#fragment")]
        public void HttpRequiresStrictHttpsOrigin(string url)
        {
            Assert.ThrowsException<ArgumentException>(() => new HttpHeartbeatTransport(new Uri(url)));
        }

        private sealed class Clock : ITestingClock
        {
            public TimeSpan Monotonic { get; private set; }
            public DateTime UtcNow { get { throw new AssertFailedException("Client wall clock must never be read."); } }
            public void Advance(double seconds) { Monotonic += TimeSpan.FromSeconds(seconds); }
        }
        private sealed class Fixture
        {
            internal readonly Guid Id = Guid.NewGuid();
            internal readonly Clock Clock = new Clock();
            internal DateTimeOffset Server = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            internal string Mode = "Training";
            internal readonly Transport Transport;
            internal Fixture() { Transport = new Transport(this); }
            internal HeartbeatReply Reply() { double seconds = Mode == "Training" ? 14400 : 7200; return new HeartbeatReply { SessionId = Id, Mode = Mode, ServerTime = Server + Clock.Monotonic, ExpiresAt = Server.AddSeconds(seconds), RemainingSeconds = seconds - Clock.Monotonic.TotalSeconds, SessionToken = "renewed" }; }
            internal SessionConnectivityMonitor Monitor(Func<TimeSpan?> elapsed = null) { return new SessionConnectivityMonitor(Id, Mode, "initial", Transport, Clock, elapsed); }
        }
        private sealed class Transport : IHeartbeatTransport
        {
            private readonly Fixture f;
            internal bool Fail, WasOffline; internal int Calls, Reject; internal long? Elapsed;
            internal Task<HeartbeatReply> Pending; internal Func<HeartbeatReply, HeartbeatReply> Transform;
            internal Transport(Fixture f) { this.f = f; }
            public Task<HeartbeatReply> SendAsync(Guid id, string token, long? elapsed, bool offline, CancellationToken ct)
            {
                Calls++; WasOffline = offline; Elapsed = elapsed;
                if (Reject != 0) throw new HeartbeatSessionRejectedException(Reject);
                if (Fail) throw new IOException("offline");
                if (Pending != null) return Pending;
                var r = f.Reply(); return Task.FromResult(Transform == null ? r : Transform(r));
            }
        }
        private sealed class Handler : HttpMessageHandler
        {
            private readonly Fixture f;
            internal string Path, Auth, Method, Text; internal JObject Body;
            internal HttpStatusCode Status = HttpStatusCode.OK; internal bool Redirect;
            internal Handler(Fixture f) { this.f = f; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Path = request.RequestUri.AbsolutePath; Auth = request.Headers.Authorization.ToString(); Method = request.Method.Method;
                Body = JObject.Parse(await request.Content.ReadAsStringAsync());
                return new HttpResponseMessage(Status) { RequestMessage = Redirect ? new HttpRequestMessage(HttpMethod.Post, "https://other.invalid/") : request, Content = new StringContent(Text ?? Newtonsoft.Json.JsonConvert.SerializeObject(f.Reply())) };
            }
        }
    }
}
