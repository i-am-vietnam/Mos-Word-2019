using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class LearnerApiTests(PostgresFixture postgres)
{
    private sealed record Learner(string Connection, string Password);

    private async Task<Learner> CreateLearnerAsync(Action<Account>? configure = null)
    {
        var connection = await postgres.CreateDatabaseAsync();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", MaxUses = 5,
            CreatedAt = DateTimeOffset.UtcNow };
        configure?.Invoke(account);
        account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, password);
        await using var db = PostgresFixture.Open(connection);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return new Learner(connection, password);
    }

    private static string Machine() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, Learner learner,
        Guid? key = null, string? machine = null, string? password = null, string username = "learner") =>
        client.PostAsJsonAsync("/api/auth/login", new { username, password = password ?? learner.Password,
            machineId = machine ?? Machine(), idempotencyKey = key ?? Guid.NewGuid() });

    private static async Task<JsonElement> ReplyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> AuthorizedPostAsync(HttpClient client, string path,
        string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ModeAsync(HttpClient client, JsonElement session, string mode) =>
        AuthorizedPostAsync(client, "/api/sessions/mode", session.GetProperty("sessionToken").GetString()!, new { mode });

    private static Task<HttpResponseMessage> ResumeAsync(HttpClient client, JsonElement session, string machine) =>
        client.PostAsJsonAsync("/api/sessions/resume", new { resumeToken = session.GetProperty("resumeToken").GetString(),
            machineId = machine });

    [Fact]
    public async Task LoginCreatesOneUnselectedSessionAndConsumesOneUse()
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var machine = Machine();
        using var response = await LoginAsync(client, learner, machine: machine, username: "LEARNER");
        var reply = await ReplyAsync(response);
        Assert.NotEqual(Guid.Empty, reply.GetProperty("sessionId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(reply.GetProperty("sessionToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(reply.GetProperty("resumeToken").GetString()));
        Assert.Equal(4, reply.GetProperty("remainingUses").GetInt32());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("mode").ValueKind);
        Assert.Equal(TimeSpan.Zero, reply.GetProperty("serverTime").GetDateTimeOffset().Offset);
        Assert.DoesNotContain(learner.Password, await response.Content.ReadAsStringAsync());
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
        var stored = await db.Sessions.SingleAsync();
        Assert.Equal(reply.GetProperty("sessionId").GetGuid(), stored.Id);
        Assert.Equal(machine, stored.MachineId);
        Assert.Null(stored.Mode);
        Assert.Null(stored.EndedAt);
    }

    [Fact]
    public async Task RetryWithSameKeyAtUseLimitReturnsOriginalSessionWithoutChargingAgain()
    {
        var learner = await CreateLearnerAsync(a => a.MaxUses = 1);
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var key = Guid.NewGuid();
        var machine = Machine();
        using var first = await LoginAsync(client, learner, key, machine);
        var original = await ReplyAsync(first);
        using var retry = await LoginAsync(client, learner, key, machine);
        var repeated = await ReplyAsync(retry);
        Assert.Equal(original.GetProperty("sessionId").GetGuid(), repeated.GetProperty("sessionId").GetGuid());
        Assert.Equal(0, repeated.GetProperty("remainingUses").GetInt32());
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
        Assert.Single(await db.Sessions.ToListAsync());
    }

    [Fact]
    public async Task SameLoginKeyCannotMoveToAnotherMachine()
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var key = Guid.NewGuid();
        using var first = await LoginAsync(client, learner, key);
        await ReplyAsync(first);
        using var conflict = await LoginAsync(client, learner, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
        Assert.Single(await db.Sessions.ToListAsync());
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("unknown")]
    [InlineData("admin")]
    [InlineData("lockout")]
    public async Task AuthenticationFailuresHaveIdenticalGenericResponseAndDoNotConsumeUses(string scenario)
    {
        var learner = await CreateLearnerAsync(a =>
        {
            if (scenario == "admin") a.IsAdmin = true;
            if (scenario == "lockout") a.LockoutUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        });
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        using var response = await LoginAsync(client, learner,
            password: scenario == "wrong-password" ? "intentionally-incorrect" : null,
            username: scenario == "unknown" ? "missing" : "learner");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_credentials", body.GetProperty("error").GetString());
        Assert.Equal("Invalid credentials. Please try again later.", body.GetProperty("message").GetString());
        Assert.DoesNotContain(learner.Password, await response.Content.ReadAsStringAsync());
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Equal(0, (await db.Accounts.SingleAsync()).Used);
        Assert.Empty(await db.Sessions.ToListAsync());
    }

    [Theory]
    [InlineData("locked", "account_locked")]
    [InlineData("expired", "account_expired")]
    [InlineData("exhausted", "no_uses_left")]
    public async Task ValidCredentialsExplainUnavailableLearnerAccount(string scenario, string error)
    {
        var learner = await CreateLearnerAsync(a =>
        {
            if (scenario == "locked") a.Locked = true;
            if (scenario == "expired") a.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (scenario == "exhausted") a.Used = a.MaxUses;
        });
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        using var response = await LoginAsync(client, learner);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(error, body.GetProperty("error").GetString());
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == "LoginFailed"));
        Assert.Empty(await db.Sessions.ToListAsync());
        Assert.Equal(scenario == "exhausted" ? 5 : 0, (await db.Accounts.SingleAsync()).Used);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha256")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task MalformedMachineCannotCreateSession(string machine)
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        using var response = await LoginAsync(client, learner, machine: machine);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Equal(0, (await db.Accounts.SingleAsync()).Used);
        Assert.Empty(await db.Sessions.ToListAsync());
    }

    [Theory]
    [InlineData("Training", "Testing")]
    [InlineData("Testing", "Training")]
    public async Task FirstModeChoiceWinsAndSameChoiceRetryIsIdempotent(string selected, string other)
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        using var login = await LoginAsync(client, learner);
        var session = await ReplyAsync(login);
        using var mode = await ModeAsync(client, session, selected);
        var reply = await ReplyAsync(mode);
        Assert.Equal(selected, reply.GetProperty("mode").GetString());
        using var retry = await ModeAsync(client, session, selected);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var conflict = await ModeAsync(client, session, other);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Equal(selected, (await db.Sessions.SingleAsync()).Mode);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
    }

    [Fact]
    public async Task InvalidModeLeavesSessionUnselected()
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        using var login = await LoginAsync(client, learner);
        var session = await ReplyAsync(login);
        using var response = await ModeAsync(client, session, "Other");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Null((await db.Sessions.SingleAsync()).Mode);
    }

    [Fact]
    public async Task ResumePreservesSessionModeAndDoesNotChargeAnotherUse()
    {
        var learner = await CreateLearnerAsync(a => a.MaxUses = 1);
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var machine = Machine();
        using var login = await LoginAsync(client, learner, machine: machine);
        var session = await ReplyAsync(login);
        using var mode = await ModeAsync(client, session, "Testing");
        Assert.Equal(HttpStatusCode.OK, mode.StatusCode);
        using var resume = await ResumeAsync(client, session, machine);
        var resumed = await ReplyAsync(resume);
        Assert.Equal(session.GetProperty("sessionId").GetGuid(), resumed.GetProperty("sessionId").GetGuid());
        Assert.Equal("Testing", resumed.GetProperty("mode").GetString());
        Assert.Equal(0, resumed.GetProperty("remainingUses").GetInt32());
        using var usable = await ModeAsync(client, resumed, "Testing");
        Assert.Equal(HttpStatusCode.OK, usable.StatusCode);
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
        Assert.Single(await db.Sessions.ToListAsync());
    }

    [Fact]
    public async Task ResumeRejectsWrongMachineAndTokensCannotSubstituteForEachOther()
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var machine = Machine();
        using var login = await LoginAsync(client, learner, machine: machine);
        var session = await ReplyAsync(login);
        using var wrongMachine = await ResumeAsync(client, session, Machine());
        Assert.Equal(HttpStatusCode.Unauthorized, wrongMachine.StatusCode);
        using var wrongBearer = await AuthorizedPostAsync(client, "/api/sessions/mode",
            session.GetProperty("resumeToken").GetString()!, new { mode = "Training" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongBearer.StatusCode);
        using var wrongResume = await client.PostAsJsonAsync("/api/sessions/resume", new
        { resumeToken = session.GetProperty("sessionToken").GetString(), machineId = machine });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResume.StatusCode);
    }

    [Fact]
    public async Task EndIsIdempotentAndEndedTokensCannotSelectModeOrResume()
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var machine = Machine();
        using var login = await LoginAsync(client, learner, machine: machine);
        var session = await ReplyAsync(login);
        var path = $"/api/sessions/{session.GetProperty("sessionId").GetGuid()}/end";
        var token = session.GetProperty("sessionToken").GetString()!;
        using var end = await AuthorizedPostAsync(client, path, token, new { });
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);
        await using var db = PostgresFixture.Open(learner.Connection);
        var endedAt = (await db.Sessions.AsNoTracking().SingleAsync()).EndedAt;
        Assert.NotNull(endedAt);
        using var retry = await AuthorizedPostAsync(client, path, token, new { });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(endedAt, (await db.Sessions.AsNoTracking().SingleAsync()).EndedAt);
        using var mode = await ModeAsync(client, session, "Training");
        Assert.Equal(HttpStatusCode.Unauthorized, mode.StatusCode);
        using var resume = await ResumeAsync(client, session, machine);
        Assert.Equal(HttpStatusCode.Unauthorized, resume.StatusCode);
        Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
    }

    [Fact]
    public async Task TokenCannotEndAnotherSessionEvenForSameAccount()
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        using var firstLogin = await LoginAsync(client, learner);
        var first = await ReplyAsync(firstLogin);
        using var secondLogin = await LoginAsync(client, learner);
        var second = await ReplyAsync(secondLogin);
        using var response = await AuthorizedPostAsync(client,
            $"/api/sessions/{second.GetProperty("sessionId").GetGuid()}/end",
            first.GetProperty("sessionToken").GetString()!, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.All(await db.Sessions.ToListAsync(), session => Assert.Null(session.EndedAt));
    }

    [Fact]
    public async Task AnonymousAndMalformedTokensCannotModifySessions()
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        using var missing = await client.PostAsJsonAsync("/api/sessions/mode", new { mode = "Training" });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var malformed = await AuthorizedPostAsync(client, "/api/sessions/mode", "invalid-token", new { mode = "Training" });
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        using var resume = await client.PostAsJsonAsync("/api/sessions/resume", new { resumeToken = "invalid-token", machineId = Machine() });
        Assert.Equal(HttpStatusCode.Unauthorized, resume.StatusCode);
        using var end = await AuthorizedPostAsync(client, $"/api/sessions/{Guid.NewGuid()}/end", "invalid-token", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, end.StatusCode);
        await using var db = PostgresFixture.Open(learner.Connection);
        Assert.Empty(await db.Sessions.ToListAsync());
        Assert.Equal(0, (await db.Accounts.SingleAsync()).Used);
    }

    [Fact]
    public async Task ConcurrentRetriesCreateExactlyOneSessionAndConsumeOneUse()
    {
        var learner = await CreateLearnerAsync(a => a.MaxUses = 1);
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var machine = Machine();
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => LoginAsync(client, learner, key, machine)));
        try
        {
            var replies = await Task.WhenAll(responses.Select(ReplyAsync));
            Assert.Single(replies.Select(r => r.GetProperty("sessionId").GetGuid()).Distinct());
            await using var db = PostgresFixture.Open(learner.Connection);
            Assert.Single(await db.Sessions.ToListAsync());
            Assert.Equal(1, (await db.Accounts.SingleAsync()).Used);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentDistinctRequestsCannotExceedUseLimit()
    {
        var learner = await CreateLearnerAsync(a => a.MaxUses = 3);
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => LoginAsync(client, learner)));
        try
        {
            Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden));
            var replies = await Task.WhenAll(responses.Where(r => r.StatusCode == HttpStatusCode.OK).Select(ReplyAsync));
            Assert.Equal(3, replies.Select(r => r.GetProperty("sessionId").GetGuid()).Distinct().Count());
            await using var db = PostgresFixture.Open(learner.Connection);
            Assert.Equal(3, (await db.Accounts.SingleAsync()).Used);
            Assert.Equal(3, await db.Sessions.CountAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("expired")]
    [InlineData("password-reset")]
    public async Task ExistingTokensAreRejectedAfterAccountRevocation(string scenario)
    {
        var learner = await CreateLearnerAsync();
        await using var factory = new ServerFactory(learner.Connection);
        using var client = factory.CreateHttpsClient();
        var machine = Machine();
        using var login = await LoginAsync(client, learner, machine: machine);
        var session = await ReplyAsync(login);
        await using (var db = PostgresFixture.Open(learner.Connection))
        {
            var account = await db.Accounts.SingleAsync();
            if (scenario == "locked") account.Locked = true;
            if (scenario == "expired") account.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (scenario == "password-reset")
            {
                account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                account.CredentialVersion = Guid.NewGuid();
            }
            await db.SaveChangesAsync();
        }
        using var mode = await ModeAsync(client, session, "Training");
        Assert.Equal(HttpStatusCode.Unauthorized, mode.StatusCode);
        using var resume = await ResumeAsync(client, session, machine);
        Assert.Equal(HttpStatusCode.Unauthorized, resume.StatusCode);
        await using var verify = PostgresFixture.Open(learner.Connection);
        Assert.Null((await verify.Sessions.SingleAsync()).Mode);
        Assert.Equal(1, (await verify.Accounts.SingleAsync()).Used);
    }
}
