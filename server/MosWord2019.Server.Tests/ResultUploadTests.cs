using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Server.Data;
using MosWord2019.Server.Packages;
using MosWord2019.Server.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class ResultUploadTests(PostgresFixture postgres)
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
        public required Session Session;
        public required Account Account;
        public required Clock Time;
        public required JObject Payload;
        public required string StorageRoot;
        public string UploadToken => Factory.Services.GetRequiredService<SessionTokens>().IssueUpload(Session.Id, Account.CredentialVersion);
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await Factory.DisposeAsync();
            var root = Path.GetFullPath(StorageRoot);
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "MosWord-C4-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected test storage path.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private async Task<Scenario> StartAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var definition = new ExamDefinition(123, Enumerable.Range(1, 7).Select(p => new ExamProjectDefinition(
            "Word2019_P" + p.ToString("00"), Enumerable.Range(1, 5).Select(t => new TaskDefinition
            { TaskId = "T" + t, InstructionKey = "instruction" + t }))));
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", MaxUses = 2, Used = 1 };
        var session = new Session { AccountId = account.Id, AccountCredentialVersion = account.CredentialVersion,
            LoginRequestId = Guid.NewGuid(), MachineId = new string('a', 64), Mode = "Testing", StartedAt = clock.Now,
            ModeSelectedAt = clock.Now, LastHeartbeatAt = clock.Now, ExamSeed = definition.Seed,
            ExamDefinitionHash = ExamDefinitionFingerprint.Compute(definition),
            ExamSnapshotJson = JsonConvert.SerializeObject(new ExamSnapshot([], [], [], JObject.FromObject(definition))) };
        await using (var db = PostgresFixture.Open(connection)) { db.AddRange(account, session); await db.SaveChangesAsync(); }
        var payload = new JObject { ["version"] = 1, ["sessionId"] = session.Id, ["seed"] = definition.Seed,
            ["machine"] = session.MachineId, ["language"] = "vi", ["definition"] = JObject.FromObject(definition),
            ["startedUtc"] = clock.Now, ["completedUtc"] = clock.Now, ["elapsedTicks"] = 0L,
            ["clockRollbackDetected"] = false, ["pendingUpload"] = true, ["total"] = -999, ["passed"] = false,
            ["tasks"] = new JArray(definition.Projects.SelectMany(p => p.TaskIds.Select(t => new JObject
            { ["projectId"] = p.ProjectId, ["taskId"] = t, ["instructionKey"] = p.InstructionKeys[t], ["outcome"] = "Pass",
                ["points"] = -1, ["awardedPoints"] = -1 }))) };
        string storageRoot = Path.Combine(Path.GetTempPath(), "MosWord-C4-" + Guid.NewGuid().ToString("N"));
        var factory = new ServerFactory(connection, new() { ["Packages:StorageRoot"] = storageRoot }, clock: clock);
        return new Scenario { Connection = connection, Factory = factory, Client = factory.CreateHttpsClient(),
            Session = session, Account = account, Time = clock, Payload = payload, StorageRoot = storageRoot };
    }
    private static Task<HttpResponseMessage> SendAsync(Scenario s, string? body = null, string? signature = null, string? token = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? s.Payload.ToString(Formatting.None));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/results") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? s.UploadToken);
        request.Headers.Add("X-Result-Signature", signature ?? Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(s.Session.ResultSigningKey), bytes)));
        return s.Client.SendAsync(request);
    }

    [Fact]
    public async Task AdminDetailUsesServerProjectScoringAndShowsCorrectCountsInsteadOfClientTotals()
    {
        await using var s = await StartAsync();
        using var uploaded = await SendAsync(s);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Guid id;
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            await AdminBootstrap.CreateFirstAsync(db, new PasswordHasher<Account>(), "owner", "admin-password-12345");
            id = (await db.Results.SingleAsync()).Id;
        }
        var login = await s.Client.GetStringAsync("/Admin/Login");
        var token = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(login,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var signed = await s.Client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Username"] = "owner", ["Password"] = "admin-password-12345", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, signed.StatusCode);
        var detail = WebUtility.HtmlDecode(await s.Client.GetStringAsync($"/Admin/Results/Details/{id}"));
        Assert.Contains("1000 / 1000", detail);
        Assert.DoesNotContain("-999", detail);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(detail, "5 / 5").Count);
        var expected = new ExamScorer(_ => { }).Score(
            ServerExamPool.Definition(JsonConvert.DeserializeObject<ExamSnapshot>(s.Session.ExamSnapshotJson!)!, s.Session.ExamSeed!.Value),
            ((JArray)s.Payload["tasks"]!).Select(t => new ExamTaskGrade((string)t["projectId"]!, (string)t["taskId"]!, new TaskGradeResult(TaskGradeOutcome.Pass, "", "", (string)t["taskId"]!))));
        foreach (var project in expected.PerProject) Assert.Contains($"{project.Points} / {project.MaximumPoints}", detail);
        await AdminHtmlCapture.SaveAsync(s.Client, "result-detail", $"/Admin/Results/Details/{id}");
    }

    [Fact]
    public async Task RecomputesScoreAndAcknowledgesExactPayloadWithoutTrustingClientTotals()
    {
        await using var s = await StartAsync();
        using var response = await SendAsync(s);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1000, (int)receipt["score"]!); Assert.True((bool)receipt["passed"]!);
        Assert.Equal(s.Session.Id, (Guid)receipt["sessionId"]!);
        Assert.Equal(JTokenType.Null, receipt["sessionToken"]!.Type);
        Assert.Equal(PackageStorage.Hash(Encoding.UTF8.GetBytes(s.Payload.ToString(Formatting.None))), (string?)receipt["payloadHash"]);
        await using var db = PostgresFixture.Open(s.Connection);
        var result = await db.Results.SingleAsync();
        Assert.Equal(1000, result.Score); Assert.True(result.Passed);
        Assert.Equal("[]", result.WrongTasksJson);
        Assert.Contains("-999", result.PayloadJson);
        Assert.DoesNotContain(s.Session.ResultSigningKey, string.Join("", await db.AuditLog.Select(a => a.DetailsJson).ToListAsync()));
    }
    [Fact]
    public async Task ConcurrentIdenticalRetriesPersistOneResultAndDifferentPayloadConflicts()
    {
        await using var s = await StartAsync();
        var responses = await Task.WhenAll(SendAsync(s), SendAsync(s));
        foreach (var response in responses) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); response.Dispose(); }
        s.Payload["total"] = 0;
        using var conflict = await SendAsync(s);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Single(await db.Results.ToListAsync());
        Assert.Single(await db.AuditLog.Where(a => a.Action == "ResultUploadConflict").ToListAsync());
    }
    [Theory]
    [InlineData("bad_signature")]
    [InlineData("wrong_session")]
    [InlineData("resume_token")]
    [InlineData("locked")]
    [InlineData("credentials")]
    public async Task RejectsUnauthorizedUploads(string reason)
    {
        await using var s = await StartAsync();
        string? token = null, signature = null;
        if (reason == "bad_signature") signature = Convert.ToBase64String(new byte[32]);
        if (reason == "wrong_session") s.Payload["sessionId"] = Guid.NewGuid();
        if (reason == "resume_token") token = s.Factory.Services.GetRequiredService<SessionTokens>().Issue(s.Session.Id, s.Account.CredentialVersion, true);
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            var account = await db.Accounts.SingleAsync();
            if (reason == "locked") account.Locked = true;
            if (reason == "credentials") account.CredentialVersion = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        using var response = await SendAsync(s, signature: signature, token: token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var reopened = PostgresFixture.Open(s.Connection); Assert.Empty(await reopened.Results.ToListAsync());
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("foreign_task")]
    [InlineData("definition")]
    [InlineData("outcome")]
    [InlineData("future_completion")]
    public async Task RejectsMalformedOrUnpinnedTaskEvidence(string reason)
    {
        await using var s = await StartAsync();
        var tasks = (JArray)s.Payload["tasks"]!;
        if (reason == "duplicate") tasks[1] = tasks[0]!.DeepClone();
        if (reason == "missing") tasks.RemoveAt(0);
        if (reason == "foreign_task") tasks[0]!["taskId"] = "other";
        if (reason == "definition") s.Payload["definition"]!["Seed"] = 999;
        if (reason == "outcome") tasks[0]!["outcome"] = "PASS";
        if (reason == "future_completion") s.Payload["completedUtc"] = s.Time.Now.AddDays(1);
        using var response = await SendAsync(s); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Theory]
    [InlineData(24, true)]
    [InlineData(24.001, false)]
    public async Task EndedTestingUsesNarrow24HourPermission(double hours, bool allowed)
    {
        await using var s = await StartAsync();
        await using (var db = PostgresFixture.Open(s.Connection))
        { var session = await db.Sessions.SingleAsync(); session.EndedAt = s.Time.Now; session.EndReason = "UserEnded"; await db.SaveChangesAsync(); }
        s.Time.Now = s.Time.Now.AddHours(hours);
        using var response = await SendAsync(s);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        using var heartbeat = await s.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/sessions/" + s.Session.Id + "/heartbeat")
        { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", s.UploadToken) }, Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        Assert.Equal(HttpStatusCode.Unauthorized, heartbeat.StatusCode);
    }
    [Fact]
    public async Task DelayedObservationEndsAbandonedSessionButStillAcceptsTimelyOfflineFinalization()
    {
        await using var s = await StartAsync();
        s.Payload["completedUtc"] = s.Time.Now.AddMinutes(50); s.Payload["elapsedTicks"] = TimeSpan.FromMinutes(50).Ticks;
        s.Time.Now = s.Time.Now.AddHours(3);
        using var response = await SendAsync(s); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal("TimeLimit", (await db.Sessions.SingleAsync()).EndReason);
        Assert.Single(await db.AuditLog.Where(a => a.Action == "SessionEnded").ToListAsync());
    }
    [Fact]
    public async Task TimingAndClockRollbackFlagsAndServerGapsAreSavedAndAudited()
    {
        await using var s = await StartAsync();
        s.Time.Now = s.Time.Now.AddMinutes(10);
        s.Payload["completedUtc"] = s.Time.Now; s.Payload["clockRollbackDetected"] = true;
        await using (var db = PostgresFixture.Open(s.Connection))
        { db.OfflineGaps.Add(new SessionOfflineGap { SessionId = s.Session.Id, StartedAt = s.Time.Now.AddMinutes(-2), EndedAt = s.Time.Now.AddMinutes(-1), Kind = "Network" }); await db.SaveChangesAsync(); }
        using var response = await SendAsync(s); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var reopened = PostgresFixture.Open(s.Connection);
        var result = await reopened.Results.SingleAsync();
        Assert.Contains("TimingInconsistent", result.FlagsJson); Assert.Contains("ClockRollbackDetected", result.FlagsJson);
        Assert.Contains("Network", result.OfflineGapsJson);
        Assert.Single(await reopened.AuditLog.Where(a => a.Action == "ResultClockRollback").ToListAsync());
    }

    [Fact]
    public async Task WrongTasksUsePinnedEncryptedPackageInstructionsAndExistingScorerWeights()
    {
        await using var s = await StartAsync();
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("lang/vi.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("{\"instruction1\":\"Áp dụng bộ kiểu Lines cho tài liệu.\",\"instruction2\":\"Chèn bảng.\"}");
        }
        var package = new Package { Code = "Word2019_P01", Version = "pinned", ZipSha256 = PackageStorage.Hash(zip.ToArray()) };
        s.Factory.Services.GetRequiredService<PackageStorage>().Store(package, zip.ToArray());
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            db.Packages.Add(package);
            var session = await db.Sessions.SingleAsync();
            var snapshot = JsonConvert.DeserializeObject<ExamSnapshot>(session.ExamSnapshotJson!)!;
            session.ExamSnapshotJson = JsonConvert.SerializeObject(snapshot with { PackageIds = [package.Id] });
            await db.SaveChangesAsync();
        }
        s.Payload["tasks"]![0]!["outcome"] = "Fail";
        s.Payload["tasks"]![1]!["outcome"] = "Error";
        using var response = await SendAsync(s); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var reopened = PostgresFixture.Open(s.Connection);
        var result = await reopened.Results.SingleAsync();
        var weights = ScoreAllocator.Allocate(35, 123);
        Assert.Equal(1000 - weights[0] - weights[1], result.Score);
        var wrong = JArray.Parse(result.WrongTasksJson); Assert.Equal(2, wrong.Count);
        Assert.Equal("Áp dụng bộ kiểu Lines cho tài liệu.", (string?)wrong[0]!["instruction"]);
        Assert.Equal("Chèn bảng.", (string?)wrong[1]!["instruction"]);
        Assert.Equal("Word2019_P01", (string?)wrong[0]!["projectId"]);
    }

    [Fact]
    public async Task UploadPurposeSharesSessionRateBudgetAndCannotAuthorizeOtherEndpoints()
    {
        await using var s = await StartAsync();
        for (int i = 0; i < 60; i++)
        { using var response = await SendAsync(s); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
        using var limited = await SendAsync(s); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection); Assert.Single(await db.Results.ToListAsync());
    }

    [Theory]
    [InlineData("Training")]
    [InlineData(null)]
    public async Task UploadNeverAuthorizesTrainingOrUnselectedMode(string? mode)
    {
        await using var s = await StartAsync();
        await using (var db = PostgresFixture.Open(s.Connection))
        { var session = await db.Sessions.SingleAsync(); session.Mode = mode; await db.SaveChangesAsync(); }
        using var response = await SendAsync(s); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AccessTokenUploadUsesCentralPolicyAndReturnsRefreshedAccess()
    {
        await using var s = await StartAsync();
        var tokens = s.Factory.Services.GetRequiredService<SessionTokens>();
        string access = tokens.Issue(s.Session.Id, s.Account.CredentialVersion);
        using var response = await SendAsync(s, token: access); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEqual(access, (string?)receipt["sessionToken"]);
        Assert.NotNull(tokens.Read((string?)receipt["sessionToken"]));
        s.Time.Now = s.Time.Now.AddMinutes(36);
        using var expired = await SendAsync(s, token: access); Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection); Assert.Equal("Abandoned", (await db.Sessions.SingleAsync()).EndReason);
    }

    [Fact]
    public async Task BodyLimitsAndDuplicateJsonPropertiesAreRejected()
    {
        await using var s = await StartAsync();
        using var duplicate = await SendAsync(s, "{\"completedUtc\":\"2026-01-01T00:00:00Z\",\"completedUtc\":\"2026-01-01T00:00:00Z\"}");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var tooLarge = await SendAsync(s, new string('x', 256 * 1024 + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
    }

    [Fact]
    public async Task LoginAndResumeIssueTheSamePersistedPerSessionDerivedKeyAcrossHosts()
    {
        await using var s = await StartAsync();
        const string password = "Test-only-password-123!";
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            var account = await db.Accounts.SingleAsync();
            account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, password);
            await db.SaveChangesAsync();
        }
        var request = new { username = "learner", password, machineId = s.Session.MachineId, idempotencyKey = s.Session.LoginRequestId };
        using var login = await s.Client.PostAsJsonAsync("/api/auth/login", request);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var reply = JObject.Parse(await login.Content.ReadAsStringAsync());
        Assert.Equal(s.Session.ResultSigningKey, (string?)reply["resultSigningKey"]);
        Assert.Equal(32, Convert.FromBase64String((string)reply["resultSigningKey"]!).Length);
        using var resume = await s.Client.PostAsJsonAsync("/api/sessions/resume", new { resumeToken = (string?)reply["resumeToken"], machineId = s.Session.MachineId });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        Assert.Equal(s.Session.ResultSigningKey, (string?)JObject.Parse(await resume.Content.ReadAsStringAsync())["resultSigningKey"]);
        await using var factory = new ServerFactory(s.Connection, clock: s.Time);
        using var client = factory.CreateHttpsClient();
        using var repeated = await client.PostAsJsonAsync("/api/auth/login", request);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(s.Session.ResultSigningKey, (string?)JObject.Parse(await repeated.Content.ReadAsStringAsync())["resultSigningKey"]);
        await using var reopened = PostgresFixture.Open(s.Connection);
        Assert.Equal(1, (await reopened.Accounts.SingleAsync()).Used);
    }

    [Fact]
    public async Task FinalizationExtendsOnlyAnExplicitlyObservedOngoingNetworkGap()
    {
        await using var s = await StartAsync();
        var began = s.Time.Now;
        s.Time.Now = began.AddMinutes(10);
        s.Payload["completedUtc"] = s.Time.Now;
        s.Payload["elapsedTicks"] = TimeSpan.FromMinutes(2).Ticks;
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            db.Heartbeats.Add(new SessionHeartbeat { SessionId = s.Session.Id, ReceivedAt = began.AddMinutes(5), WasOffline = true });
            db.OfflineGaps.Add(new SessionOfflineGap { SessionId = s.Session.Id, StartedAt = began.AddMinutes(2), EndedAt = began.AddMinutes(5), Kind = "Network" });
            await db.SaveChangesAsync();
        }
        using var response = await SendAsync(s); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var reopened = PostgresFixture.Open(s.Connection);
        var result = await reopened.Results.SingleAsync(); Assert.DoesNotContain("TimingInconsistent", result.FlagsJson);
        Assert.Equal(s.Time.Now, (await reopened.OfflineGaps.SingleAsync()).EndedAt);
    }
}
