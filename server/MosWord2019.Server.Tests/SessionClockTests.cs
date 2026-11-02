using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class SessionClockTests(PostgresFixture postgres)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(string Connection, string Password)> AccountAsync(bool admin = false)
    {
        var connection = await postgres.CreateDatabaseAsync();
        var password = Guid.NewGuid().ToString("N");
        await using var db = PostgresFixture.Open(connection);
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", IsAdmin = admin, MaxUses = 4 };
        account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, password);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return (connection, password);
    }

    private static WebApplicationFactory<Program> WithClock(ServerFactory factory, Clock clock) => factory.WithWebHostBuilder(
        builder => builder.ConfigureServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }));

    private static async Task<bool> AttemptAsync(string connection, AdminAccess access, bool admin, string password, DateTimeOffset now)
    {
        await using var db = PostgresFixture.Open(connection);
        if (admin) return await access.AuthenticateAsync(db, "learner", password, now, default) != null;
        var service = new LearnerSessions(db, access, new SessionTokens(new EphemeralDataProtectionProvider()), new Clock(now));
        var result = await service.LoginAsync(new LoginRequest("learner", password, new string('a', 64), Guid.NewGuid()), default);
        return result.Status == 200;
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ResumeUsesServerTimeAndIncludesExactFifteenMinuteBoundary(long extraTicks, bool allowed)
    {
        var (connection, password) = await AccountAsync();
        var clock = new Clock(DateTimeOffset.UtcNow);
        await using var root = new ServerFactory(connection);
        await using var factory = WithClock(root, clock);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var start = clock.Now;
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        { username = "learner", password, machineId = new string('a', 64), idempotencyKey = Guid.NewGuid(), clientTime = start.AddYears(50) });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(start, body.GetProperty("serverTime").GetDateTimeOffset());
        clock.Now = start.AddMinutes(15).AddTicks(extraTicks);
        using var resumed = await client.PostAsJsonAsync("/api/sessions/resume", new
        { resumeToken = body.GetProperty("resumeToken").GetString(), machineId = new string('a', 64), clientTime = start });
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, resumed.StatusCode);
        await using var db = PostgresFixture.Open(connection);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
        Assert.Equal(start, (await db.Sessions.SingleAsync()).StartedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TenConsecutiveFailuresLockForFifteenMinutesThenSuccessResetsCounters(bool admin)
    {
        var (connection, password) = await AccountAsync(admin);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var access = new AdminAccess(new PasswordHasher<Account>());
        for (var i = 0; i < 10; i++)
        {
            Assert.False(await AttemptAsync(connection, access, admin, "incorrect", now));
        }
        await using (var db = PostgresFixture.Open(connection))
        {
            var account = await db.Accounts.SingleAsync();
            Assert.Equal(10, account.FailedLoginCount);
            Assert.Equal(now.AddMinutes(15), account.LockoutUntil);
            Assert.Equal(10, await db.AuditLog.CountAsync(x => x.Action == "LoginFailed"));
            Assert.Single(await db.AuditLog.Where(x => x.Action == "AccountTemporarilyLocked").ToListAsync());
        }
        Assert.False(await AttemptAsync(connection, access, admin, password, now.AddMinutes(14)));
        Assert.True(await AttemptAsync(connection, access, admin, password, now.AddMinutes(15)));
        await using (var db = PostgresFixture.Open(connection))
        {
            var account = await db.Accounts.SingleAsync();
            Assert.Equal(0, account.FailedLoginCount);
            Assert.Null(account.LockoutUntil);
        }
    }

    [Fact]
    public async Task SuccessfulLoginBetweenFailuresResetsConsecutiveCount()
    {
        var (connection, password) = await AccountAsync();
        var access = new AdminAccess(new PasswordHasher<Account>());
        foreach (var candidate in new[] { "incorrect", password, "incorrect" })
        {
            await AttemptAsync(connection, access, false, candidate, DateTimeOffset.UtcNow);
        }
        await using var reopened = PostgresFixture.Open(connection);
        Assert.Equal(1, (await reopened.Accounts.SingleAsync()).FailedLoginCount);
    }
    [Fact]
    public async Task LearnerLoginCreatesOneUseAndSession()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var password = Guid.NewGuid().ToString("N");
        await using (var db = PostgresFixture.Open(connection))
        {
            var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", MaxUses = 2 };
            account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, password);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
        }
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        { username = "learner", password, machineId = new string('a', 64), idempotencyKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
