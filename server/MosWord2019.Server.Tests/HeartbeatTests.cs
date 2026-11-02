using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class HeartbeatTests(PostgresFixture postgres)
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Scenario : IAsyncDisposable
    {
        public required string Connection;
        public required ServerFactory Factory;
        public required HttpClient Client;
        public required Clock Time;
        public required Session Session;
        public required Account Account;
        public required string Token;
        public required string ResumeToken;
        public async ValueTask DisposeAsync() { Client.Dispose(); await Factory.DisposeAsync(); }
    }

    private async Task<Scenario> StartAsync(string? mode = "Testing")
    {
        string connection = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", MaxUses = 5, Used = 1 };
        account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, "Test-Only-Password-123!");
        var session = new Session { AccountId = account.Id, AccountCredentialVersion = account.CredentialVersion,
            LoginRequestId = Guid.NewGuid(),
            MachineId = new string('a', 64), StartedAt = clock.Now, ModeSelectedAt = mode == null ? null : clock.Now,
            LastHeartbeatAt = clock.Now, Mode = mode };
        await using (var db = PostgresFixture.Open(connection)) { db.AddRange(account, session); await db.SaveChangesAsync(); }
        var factory = new ServerFactory(connection, clock: clock);
        var tokens = factory.Services.GetRequiredService<SessionTokens>();
        return new Scenario { Connection = connection, Time = clock, Session = session, Account = account, Factory = factory,
            Client = factory.CreateHttpsClient(), Token = tokens.Issue(session.Id, account.CredentialVersion),
            ResumeToken = tokens.Issue(session.Id, account.CredentialVersion, true) };
    }

    private static Task<HttpResponseMessage> BeatAsync(Scenario s, long? elapsed = null, bool offline = false, Guid? id = null,
        string? token = null) => PostAsync(s, "/api/sessions/" + (id ?? s.Session.Id) + "/heartbeat",
            new { clientElapsedMilliseconds = elapsed, wasOffline = offline }, token);
    private static Task<HttpResponseMessage> PostAsync(Scenario s, string path, object body, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? s.Token);
        return s.Client.SendAsync(request);
    }

    [Theory]
    [InlineData("Training", 14370)]
    [InlineData("Testing", 7170)]
    public async Task HeartbeatPersistsServerUtcAndReturnsDeadlineAndRefreshedToken(string mode, int remaining)
    {
        await using var s = await StartAsync(mode);
        s.Time.Now = s.Time.Now.AddSeconds(30);
        using var response = await BeatAsync(s);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(s.Session.Id, reply.GetProperty("sessionId").GetGuid());
        Assert.Equal(s.Time.Now, reply.GetProperty("serverTime").GetDateTimeOffset());
        Assert.Equal(remaining, reply.GetProperty("remainingSeconds").GetDouble());
        Assert.Equal(mode, reply.GetProperty("mode").GetString());
        Assert.NotEqual(s.Token, reply.GetProperty("sessionToken").GetString());
        Assert.NotNull(s.Factory.Services.GetRequiredService<SessionTokens>().Read(reply.GetProperty("sessionToken").GetString()));
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal(s.Time.Now, (await db.Sessions.SingleAsync()).LastHeartbeatAt);
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM \"Heartbeats\"").SingleAsync());
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
    }

    [Fact]
    public async Task TenFailedLoginsDoNotEndActiveExamButAdminLockDoes()
    {
        await using var s = await StartAsync();
        for (int i = 0; i < 10; i++)
        {
            using var wrong = await s.Client.PostAsJsonAsync("/api/auth/login", new { username = "learner", password = "wrong",
                machineId = new string('b', 64), idempotencyKey = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.True((await db.Accounts.SingleAsync()).LockoutUntil > s.Time.Now);
        using var resume = await s.Client.PostAsJsonAsync("/api/sessions/resume", new { resumeToken = s.ResumeToken, machineId = s.Session.MachineId });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        using var heartbeat = await BeatAsync(s);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.Null((await db.Sessions.AsNoTracking().SingleAsync()).EndedAt);
        Assert.Equal(0, await db.AuditLog.CountAsync(a => a.Action == "SessionEnded"));
        var account = await db.Accounts.SingleAsync(); account.Locked = true; await db.SaveChangesAsync();
        using var denied = await BeatAsync(s);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("Expired", (await db.Sessions.AsNoTracking().SingleAsync()).EndReason);
        Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == "SessionEnded"));
        Assert.Equal(1, (await db.Accounts.AsNoTracking().SingleAsync()).Used);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("expired")]
    [InlineData("credential")]
    public async Task ResumeStillRejectsRevokedAccountAndEndsSessionOnlyOnce(string reason)
    {
        await using var s = await StartAsync();
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            var account = await db.Accounts.SingleAsync();
            account.LockoutUntil = s.Time.Now.AddMinutes(15);
            if (reason == "locked") account.Locked = true;
            if (reason == "expired") account.ExpiresAt = s.Time.Now;
            if (reason == "credential") account.CredentialVersion = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var denied = await s.Client.PostAsJsonAsync("/api/sessions/resume",
                new { resumeToken = s.ResumeToken, machineId = s.Session.MachineId });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        await using var reopened = PostgresFixture.Open(s.Connection);
        Assert.Equal("Expired", (await reopened.Sessions.SingleAsync()).EndReason);
        Assert.Equal(1, await reopened.AuditLog.CountAsync(x => x.Action == "SessionEnded"));
        Assert.Equal(0, await reopened.AuditLog.CountAsync(x => x.Action == "SessionResumed"));
        Assert.Equal(1, (await reopened.Accounts.SingleAsync()).Used);
    }

    [Fact]
    public async Task ResumeDuringPasswordLockoutStillRequiresSecretAndSameMachine()
    {
        await using var s = await StartAsync();
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            var account = await db.Accounts.SingleAsync();
            account.LockoutUntil = s.Time.Now.AddMinutes(15);
            account.FailedLoginCount = 10;
            await db.SaveChangesAsync();
        }
        foreach (var request in new[]
        {
            new ResumeRequest(s.ResumeToken, new string('b', 64)),
            new ResumeRequest(s.Token, s.Session.MachineId),
            new ResumeRequest("invalid", s.Session.MachineId)
        })
        {
            using var denied = await s.Client.PostAsJsonAsync("/api/sessions/resume", request);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var resumed = await s.Client.PostAsJsonAsync("/api/sessions/resume",
            new ResumeRequest(s.ResumeToken, s.Session.MachineId));
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        await using var reopened = PostgresFixture.Open(s.Connection);
        Assert.Null((await reopened.Sessions.SingleAsync()).EndedAt);
        Assert.Equal(1, await reopened.AuditLog.CountAsync(x => x.Action == "SessionResumed"));
        var persisted = await reopened.Accounts.SingleAsync();
        Assert.Equal(10, persisted.FailedLoginCount);
        Assert.True(persisted.LockoutUntil > s.Time.Now);
        Assert.Equal(1, persisted.Used);
    }

    [Fact]
    public async Task SixPerMinuteBucketIsSharedByRefreshedTokensButSeparateBetweenSessions()
    {
        await using var s = await StartAsync();
        for (int i = 0; i < 6; i++)
        {
            using var response = await BeatAsync(s);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            s.Token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionToken").GetString()!;
        }
        using var seventh = await BeatAsync(s); Assert.Equal(HttpStatusCode.TooManyRequests, seventh.StatusCode);
        var other = new Session { AccountId = s.Account.Id, AccountCredentialVersion = s.Account.CredentialVersion,
            LoginRequestId = Guid.NewGuid(),
            MachineId = new string('b', 64), StartedAt = s.Time.Now, ModeSelectedAt = s.Time.Now, LastHeartbeatAt = s.Time.Now, Mode = "Testing" };
        await using (var db = PostgresFixture.Open(s.Connection)) { db.Sessions.Add(other); await db.SaveChangesAsync(); }
        string token = s.Factory.Services.GetRequiredService<SessionTokens>().Issue(other.Id, s.Account.CredentialVersion);
        using var separate = await BeatAsync(s, id: other.Id, token: token); Assert.Equal(HttpStatusCode.OK, separate.StatusCode);
    }

    [Fact]
    public async Task WrongSessionOrResumeTokenCannotHeartbeatAndPendingCannotBeKeptAlive()
    {
        await using var s = await StartAsync(null);
        using var wrong = await BeatAsync(s, id: Guid.NewGuid()); Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var resume = await BeatAsync(s, token: s.ResumeToken); Assert.Equal(HttpStatusCode.Unauthorized, resume.StatusCode);
        using var pending = await BeatAsync(s); Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        s.Time.Now = s.Time.Now.AddMinutes(15);
        using var expired = await BeatAsync(s); Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task NetworkGapIsRecordedAndSubtractedButCrashDowntimeStillCounts()
    {
        foreach (bool offline in new[] { false, true })
        {
            await using var s = await StartAsync();
            using var first = await BeatAsync(s, 0); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            s.Time.Now = s.Time.Now.AddSeconds(120);
            using var next = await BeatAsync(s, offline ? 90_000 : 120_000, offline);
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            await using var db = PostgresFixture.Open(s.Connection);
            var gap = await db.OfflineGaps.SingleAsync();
            Assert.Equal(offline ? "Network" : "Unobserved", gap.Kind);
            Assert.Equal(TimeSpan.FromSeconds(30), gap.EndedAt - gap.StartedAt);
            Assert.False((await db.Sessions.SingleAsync()).TimingInconsistent);
        }
    }

    [Fact]
    public async Task LostResponsesStillRecordNetworkPauseWithoutFalseTimingFlag()
    {
        await using var s = await StartAsync();
        var start = s.Time.Now;
        foreach (int seconds in new[] { 0, 30, 60, 90, 120, 150, 180, 210 })
        {
            s.Time.Now = start.AddSeconds(seconds);
            using var scope = s.Factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<SessionHeartbeats>();
            var reply = await service.BeatAsync(s.Session.Id, s.Token,
                new HeartbeatRequest(Math.Min(seconds, 90) * 1000L, seconds >= 120), default);
            Assert.Equal(200, reply.Status);
        }
        await using var db = PostgresFixture.Open(s.Connection);
        var gap = await db.OfflineGaps.SingleAsync();
        Assert.Equal("Network", gap.Kind);
        Assert.Equal(start.AddSeconds(120), gap.StartedAt);
        Assert.Equal(start.AddSeconds(210), gap.EndedAt);
        Assert.False((await db.Sessions.SingleAsync()).TimingInconsistent);
        Assert.Equal(0, await db.AuditLog.CountAsync(a => a.Action == "TestingTimingFlagged"));
    }

    [Fact]
    public async Task RepeatedShortResponseLossesRetainPerOutageUncertainty()
    {
        await using var s = await StartAsync();
        var start = s.Time.Now;
        int pausedSeconds = 0;
        foreach (int seconds in Enumerable.Range(0, 25).Select(i => i * 30))
        {
            s.Time.Now = start.AddSeconds(seconds);
            bool offline = seconds > 0 && seconds % 120 == 0;
            if (offline) pausedSeconds += 20;
            using var scope = s.Factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<SessionHeartbeats>();
            var reply = await service.BeatAsync(s.Session.Id, s.Token,
                new HeartbeatRequest((seconds - pausedSeconds) * 1000L, offline), default);
            Assert.Equal(200, reply.Status);
        }
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal(6, await db.OfflineGaps.CountAsync(g => g.Kind == "Network"));
        Assert.False((await db.Sessions.SingleAsync()).TimingInconsistent);
    }

    [Fact]
    public async Task ResumeRecordsUnobservedGapAndDoesNotSubtractCrashDowntime()
    {
        await using var s = await StartAsync();
        using var first = await BeatAsync(s, 0); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        s.Time.Now = s.Time.Now.AddMinutes(3);
        using var resume = await s.Client.PostAsJsonAsync("/api/sessions/resume", new { resumeToken = s.ResumeToken, machineId = s.Session.MachineId });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        using var next = await BeatAsync(s, 180_000); Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal("Unobserved", (await db.OfflineGaps.SingleAsync()).Kind);
        Assert.False((await db.Sessions.SingleAsync()).TimingInconsistent);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
    }

    [Fact]
    public async Task RulesWaitingIsExcludedAndTimingFlagIsStickyAndAuditedOnlyOnce()
    {
        await using var s = await StartAsync();
        s.Time.Now = s.Time.Now.AddMinutes(4);
        using var start = await BeatAsync(s, 0);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.False((await db.Sessions.AsNoTracking().SingleAsync()).TimingInconsistent);
        s.Time.Now = s.Time.Now.AddMinutes(5);
        using var frozen = await BeatAsync(s, 0); Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
        Assert.True((await db.Sessions.AsNoTracking().SingleAsync()).TimingInconsistent);
        s.Time.Now = s.Time.Now.AddSeconds(30);
        using var again = await BeatAsync(s, 0); Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == "TestingTimingFlagged"));
        Assert.Null((await db.Sessions.AsNoTracking().SingleAsync()).EndedAt);
    }

    [Theory]
    [InlineData("Training", 240)]
    [InlineData("Testing", 120)]
    public async Task HeartbeatCannotExtendHardDeadline(string mode, int minutes)
    {
        await using var s = await StartAsync(mode);
        s.Time.Now = s.Time.Now.AddMinutes(minutes).AddSeconds(-1);
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            var session = await db.Sessions.SingleAsync(); session.LastHeartbeatAt = s.Time.Now;
            await db.SaveChangesAsync();
        }
        s.Token = s.Factory.Services.GetRequiredService<SessionTokens>().Issue(s.Session.Id, s.Account.CredentialVersion);
        using var before = await BeatAsync(s); Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(1, (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("remainingSeconds").GetDouble());
        s.Time.Now = s.Time.Now.AddSeconds(1);
        using var deadline = await BeatAsync(s); Assert.Equal(HttpStatusCode.Unauthorized, deadline.StatusCode);
        await using var reopened = PostgresFixture.Open(s.Connection);
        Assert.Equal("TimeLimit", (await reopened.Sessions.SingleAsync()).EndReason);
        Assert.Equal(1, await reopened.Heartbeats.CountAsync());
    }

    [Fact]
    public async Task ConcurrentHeartbeatsSerializeWithoutConsumingUses()
    {
        await using var s = await StartAsync();
        var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => BeatAsync(s)));
        foreach (var reply in replies) { Assert.Equal(HttpStatusCode.OK, reply.StatusCode); reply.Dispose(); }
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal(4, await db.Heartbeats.CountAsync());
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
        Assert.Null((await db.Sessions.SingleAsync()).EndedAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86400001)]
    public async Task InvalidClientTelemetryCannotUpdateContactOrUses(long elapsed)
    {
        await using var s = await StartAsync();
        s.Time.Now = s.Time.Now.AddSeconds(30);
        using var response = await BeatAsync(s, elapsed); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal(s.Session.LastHeartbeatAt, (await db.Sessions.SingleAsync()).LastHeartbeatAt);
        Assert.Empty(await db.Heartbeats.ToListAsync());
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
    }

    [Fact]
    public async Task ClosedAndIdleSessionsCannotBeRevivedByHeartbeat()
    {
        await using var s = await StartAsync();
        s.Time.Now = s.Time.Now.AddMinutes(15).AddMilliseconds(1);
        using var expired = await BeatAsync(s); Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal("Abandoned", (await db.Sessions.SingleAsync()).EndReason);
        using var repeated = await BeatAsync(s); Assert.Equal(HttpStatusCode.Unauthorized, repeated.StatusCode);
        Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == "SessionEnded"));
    }
}
