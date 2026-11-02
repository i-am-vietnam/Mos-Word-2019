using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class SessionPolicyTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
    private static (Account Account, Session Session) State(string? mode)
    {
        var account = new Account();
        return (account, new Session { AccountId = account.Id, AccountCredentialVersion = account.CredentialVersion,
            StartedAt = Start, LastHeartbeatAt = Start, Mode = mode, ModeSelectedAt = mode == null ? null : Start });
    }

    [Theory]
    [InlineData(null, 15, "Expired")]
    [InlineData("Training", 240, "TimeLimit")]
    [InlineData("Testing", 120, "TimeLimit")]
    public void ModeDeadlinesAreExclusive(string? mode, int minutes, string reason)
    {
        var (account, session) = State(mode);
        var deadline = Start.AddMinutes(minutes);
        session.LastHeartbeatAt = deadline.AddTicks(-1);
        Assert.True(SessionPolicy.Evaluate(account, session, deadline.AddTicks(-1), account.CredentialVersion).Allowed);
        var denied = SessionPolicy.Evaluate(account, session, deadline, account.CredentialVersion);
        Assert.False(denied.Allowed);
        Assert.Equal(reason, denied.EndReason);
    }

    [Fact]
    public void TestingDeadlineStartsAtModeSelection()
    {
        var (account, session) = State("Testing");
        session.ModeSelectedAt = Start.AddMinutes(10);
        session.LastHeartbeatAt = Start.AddMinutes(120);
        Assert.True(SessionPolicy.Evaluate(account, session, Start.AddMinutes(120), account.CredentialVersion).Allowed);
        Assert.Equal("TimeLimit", SessionPolicy.Evaluate(account, session, Start.AddMinutes(130), account.CredentialVersion).EndReason);
    }

    [Fact]
    public void IdleBoundaryIsInclusiveAndAbandonmentIsPermanent()
    {
        var (account, session) = State("Training");
        Assert.True(SessionPolicy.Evaluate(account, session, Start.AddMinutes(15), account.CredentialVersion).Allowed);
        var denied = SessionPolicy.Evaluate(account, session, Start.AddMinutes(15).AddTicks(1), account.CredentialVersion);
        Assert.Equal("Abandoned", denied.EndReason);
        var audit = SessionPolicy.End(session, Start.AddMinutes(16), denied.EndReason!);
        Assert.NotNull(audit);
        Assert.Contains(session.Id.ToString(), audit.DetailsJson);
        Assert.Null(SessionPolicy.End(session, Start.AddMinutes(17), "UserEnded"));
        session.LastHeartbeatAt = Start.AddMinutes(17);
        Assert.False(SessionPolicy.Evaluate(account, session, Start.AddMinutes(17), account.CredentialVersion).Allowed);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("locked")]
    [InlineData("expired")]
    [InlineData("lockout")]
    [InlineData("credential")]
    public void ActiveAuthorizationChecksAccount(string denial)
    {
        var (account, session) = State("Training");
        if (denial == "role") account.IsAdmin = true;
        if (denial == "locked") account.Locked = true;
        if (denial == "expired") account.ExpiresAt = Start;
        if (denial == "lockout") account.LockoutUntil = Start.AddMinutes(1);
        if (denial == "credential") account.CredentialVersion = Guid.NewGuid();
        Assert.False(SessionPolicy.Evaluate(account, session, Start, session.AccountCredentialVersion).Allowed);
    }

    [Fact]
    public void UploadPermissionIsSeparateAndSurvivesAccountExpiryOnlyWithinWindow()
    {
        var (account, session) = State("Testing");
        SessionPolicy.End(session, Start.AddHours(1), "UserEnded");
        account.ExpiresAt = Start.AddHours(2);
        Assert.True(SessionPolicy.CanUpload(account, session, Start.AddHours(24), account.CredentialVersion, Start.AddHours(1)));
        Assert.True(SessionPolicy.CanUpload(account, session, Start.AddHours(25), account.CredentialVersion, Start.AddHours(1)));
        Assert.False(SessionPolicy.CanUpload(account, session, Start.AddHours(24), account.CredentialVersion, Start.AddTicks(-1)));
        Assert.False(SessionPolicy.CanUpload(account, session, Start.AddHours(24), account.CredentialVersion, Start.AddHours(1).AddTicks(1)));
        Assert.False(SessionPolicy.Evaluate(account, session, Start.AddHours(24), account.CredentialVersion).Allowed);
        Assert.False(SessionPolicy.CanUpload(account, session, Start.AddHours(25).AddTicks(1), account.CredentialVersion, Start.AddHours(1)));
        account.CredentialVersion = Guid.NewGuid();
        Assert.False(SessionPolicy.CanUpload(account, session, Start.AddHours(24), session.AccountCredentialVersion, Start.AddHours(1)));
    }

    [Fact]
    public void ForeignCredentialIdentityCannotEndCurrentSession()
    {
        var (account, session) = State("Training");
        var result = SessionPolicy.Evaluate(account, session, Start, Guid.NewGuid());
        Assert.False(result.Allowed);
        Assert.Null(result.EndReason);
    }

    [Fact]
    public void DelayedEndObservationCannotExtendTestingFinalizationOrUploadDeadlines()
    {
        var (account, session) = State("Testing");
        SessionPolicy.End(session, Start.AddHours(48), "TimeLimit");
        Assert.False(SessionPolicy.CanUpload(account, session, Start.AddHours(48), account.CredentialVersion,
            Start.AddHours(1)));
        Assert.False(SessionPolicy.CanUpload(account, session, Start.AddHours(48), account.CredentialVersion,
            Start.AddHours(3)));
        session.EndedAt = Start.AddHours(4);
        Assert.False(SessionPolicy.CanUpload(account, session, Start.AddHours(4), account.CredentialVersion,
            Start.AddHours(3)));
        Assert.True(SessionPolicy.CanUpload(account, session, Start.AddHours(26), account.CredentialVersion,
            Start.AddHours(2)));
        Assert.False(SessionPolicy.CanUpload(account, session, Start.AddHours(26).AddTicks(1), account.CredentialVersion,
            Start.AddHours(2)));
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 255)]
    public void TokensExpireAtServerDeadlineAndCanBeRefreshed(bool resume, int minutes)
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var tokens = new SessionTokens(new EphemeralDataProtectionProvider(), clock);
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        var token = tokens.Issue(id, version, resume);
        Assert.Null(tokens.Read(token, !resume));
        Assert.Null(tokens.ReadExpirationHint(token, !resume));
        clock.Now = clock.Now.AddMinutes(minutes).AddTicks(-1);
        Assert.Equal(id, tokens.Read(token, resume)!.SessionId);
        var refreshed = tokens.Issue(id, version, resume);
        clock.Now = clock.Now.AddTicks(1);
        Assert.Null(tokens.Read(token, resume));
        Assert.Equal(id, tokens.ReadExpirationHint(token, resume)!.SessionId);
        Assert.NotNull(tokens.Read(refreshed, resume));
    }

    [Fact]
    public void UploadTokenHasSeparatePurposeAndBoundedLifetime()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var tokens = new SessionTokens(new EphemeralDataProtectionProvider(), clock);
        var token = tokens.IssueUpload(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(tokens.Read(token));
        Assert.Null(tokens.Read(token, true));
        clock.Now = clock.Now.AddMinutes(1575).AddTicks(-1);
        Assert.NotNull(tokens.ReadUpload(token));
        clock.Now = clock.Now.AddTicks(1);
        Assert.Null(tokens.ReadUpload(token));
    }

    [Fact]
    public async Task KilledClientCannotAuthorizeThreeHoursLaterAndEndAuditPersistsOnce()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var tokens = new SessionTokens(new EphemeralDataProtectionProvider(), clock);
        Guid id;
        Guid version;
        await using (var db = PostgresFixture.Open(connection))
        {
            var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", MaxUses = 2 };
            version = account.CredentialVersion;
            var session = new Session { AccountId = account.Id, AccountCredentialVersion = version,
                MachineId = new string('a', 64), StartedAt = clock.Now, LastHeartbeatAt = clock.Now,
                Mode = "Training", ModeSelectedAt = clock.Now };
            id = session.Id;
            db.AddRange(account, session);
            await db.SaveChangesAsync();
        }
        var accessToken = tokens.Issue(id, version);
        clock.Now = clock.Now.AddHours(3);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = PostgresFixture.Open(connection);
            var service = new LearnerSessions(db, new AdminAccess(new PasswordHasher<Account>()), tokens, clock);
            var result = await service.ModeAsync(accessToken, new ModeRequest("Training"), default);
            Assert.Equal(401, result.Status);
        }
        await using var reopened = PostgresFixture.Open(connection);
        Assert.Equal("Abandoned", (await reopened.Sessions.SingleAsync()).EndReason);
        Assert.Single(await reopened.AuditLog.Where(x => x.Action == "SessionEnded").ToListAsync());
    }

    [Fact]
    public async Task LoginReplayCannotReviveIdleSessionAndUserEndReceiptGrantsNoActiveAccess()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var tokens = new SessionTokens(new EphemeralDataProtectionProvider(), clock);
        await using var db = PostgresFixture.Open(connection);
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", MaxUses = 3 };
        var hasher = new PasswordHasher<Account>();
        account.PasswordHash = hasher.HashPassword(account, "password");
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var service = new LearnerSessions(db, new AdminAccess(hasher), tokens, clock);
        var request = new LoginRequest("learner", "password", new string('a', 64), Guid.NewGuid());
        var login = Assert.IsType<SessionReply>((await service.LoginAsync(request, default)).Body);
        Assert.Equal(200, (await service.ModeAsync(login.SessionToken, new ModeRequest("Training"), default)).Status);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Equal(409, (await service.LoginAsync(request, default)).Status);
        Assert.Equal("Abandoned", (await db.Sessions.SingleAsync()).EndReason);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);

        var next = Assert.IsType<SessionReply>((await service.LoginAsync(request with { IdempotencyKey = Guid.NewGuid() }, default)).Body);
        Assert.Equal(200, (await service.EndAsync(next.SessionToken, next.SessionId, default)).Status);
        Assert.Equal(200, (await service.EndAsync(next.SessionToken, next.SessionId, default)).Status);
        Assert.Equal(401, (await service.ModeAsync(next.SessionToken, new ModeRequest("Training"), default)).Status);
        Assert.Equal(401, (await service.ResumeAsync(new ResumeRequest(next.ResumeToken, request.MachineId), default)).Status);
        Assert.Equal(2, await db.AuditLog.CountAsync(x => x.Action == "SessionEnded"));
    }

    [Fact]
    public async Task ExpiredTokenHintCannotAuthorizeOrEndAnOtherwiseActiveSession()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var tokens = new SessionTokens(new EphemeralDataProtectionProvider(), clock);
        await using var db = PostgresFixture.Open(connection);
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", MaxUses = 1 };
        var session = new Session { AccountId = account.Id, AccountCredentialVersion = account.CredentialVersion,
            MachineId = new string('a', 64), StartedAt = clock.Now, Mode = "Training", ModeSelectedAt = clock.Now };
        var token = tokens.Issue(session.Id, account.CredentialVersion);
        clock.Now = clock.Now.AddMinutes(31);
        session.LastHeartbeatAt = clock.Now;
        db.AddRange(account, session);
        await db.SaveChangesAsync();
        var service = new LearnerSessions(db, new AdminAccess(new PasswordHasher<Account>()), tokens, clock);
        Assert.Equal(401, (await service.ModeAsync(token, new ModeRequest("Training"), default)).Status);
        Assert.Null((await db.Sessions.SingleAsync()).EndedAt);
        Assert.Empty(await db.AuditLog.ToListAsync());
    }
}
