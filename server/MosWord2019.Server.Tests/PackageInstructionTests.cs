using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Server.Data;
using MosWord2019.Server.Packages;
using MosWord2019.Server.ResultHandling;
using MosWord2019.Server.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Server.Tests;

public sealed class PackageJsonTests
{
    private static JObject Parse(byte[] bytes) => PackageJson.Parse(bytes);

    [Fact]
    public void SingleLeadingUtf8BomAndNoBomProduceIdenticalInstructions()
    {
        var plain = Encoding.UTF8.GetBytes("{\"instruction\":\"Áp dụng bộ kiểu cho tài liệu.\"}");
        Assert.True(JToken.DeepEquals(Parse(plain), Parse([0xef, 0xbb, 0xbf, .. plain])));
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("duplicate")]
    [InlineData("trailing-object")]
    [InlineData("trailing-token")]
    [InlineData("invalid-utf8")]
    [InlineData("double-bom")]
    [InlineData("depth")]
    [InlineData("size")]
    public void PackageParserKeepsStrictValidation(string kind)
    {
        byte[] bytes = kind switch
        {
            "invalid-json" => Encoding.UTF8.GetBytes("{broken"),
            "duplicate" => Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"),
            "trailing-object" => Encoding.UTF8.GetBytes("{} {}"),
            "trailing-token" => Encoding.UTF8.GetBytes("{} false"),
            "invalid-utf8" => [0x7b, 0x22, 0x61, 0x22, 0x3a, 0x22, 0xc3, 0x28, 0x22, 0x7d],
            "double-bom" => [0xef, 0xbb, 0xbf, 0xef, 0xbb, 0xbf, 0x7b, 0x7d],
            "depth" => Encoding.UTF8.GetBytes("{\"a\":" + new string('[', 65) + "0" + new string(']', 65) + "}"),
            "size" => Encoding.UTF8.GetBytes("{\"a\":\"" + new string('x', 4 * 1024 * 1024) + "\"}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.NotNull(Record.Exception(() => Parse(bytes)));
        Assert.NotNull(Record.Exception(() => Parse([0xef, 0xbb, 0xbf, .. bytes])));
    }

    [Fact]
    public void ResultPayloadParserStillRejectsLeadingBom()
    {
        Assert.NotNull(Record.Exception(() => ResultValidation.Parse([0xef, 0xbb, 0xbf, 0x7b, 0x7d])));
    }

    [Fact]
    public void PackageParserAcceptsInclusiveFourMiBSizeLimit()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{}" + new string(' ', 4 * 1024 * 1024 - 2));
        Assert.Empty(Parse(bytes));
    }
}

[Collection("PostgreSQL")]
public sealed class PackageInstructionTests(PostgresFixture postgres)
{
    private sealed class CapturedLogs : ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public readonly ConcurrentQueue<Dictionary<string, object?>> States = new();
        public ILogger CreateLogger(string categoryName) => new Capture(Lines, States);
        public void Dispose() { }
        private sealed class Capture(ConcurrentQueue<string> lines, ConcurrentQueue<Dictionary<string, object?>> states) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning) return;
                lines.Enqueue(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                    states.Enqueue(fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required string Connection;
        public required string Root;
        public required ServerFactory BaseFactory;
        public required Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory;
        public required HttpClient Client;
        public required List<Package> Packages;
        public required CapturedLogs Logs;
        public PackageStorage Storage => Factory.Services.GetRequiredService<PackageStorage>();
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await Factory.DisposeAsync(); await BaseFactory.DisposeAsync();
            var root = Path.GetFullPath(Root);
            if (!root.StartsWith(Path.Combine(Path.GetTempPath(), "MosWord-C7Fix2-"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected disposable storage root.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string ProjectsRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, ".git")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found.");
        return Path.Combine(root, "MosWord2019.WinForms", "Projects");
    }

    private async Task<Scenario> ImportRealPackagesAsync()
    {
        string connection = await postgres.CreateDatabaseAsync();
        string root = Path.Combine(Path.GetTempPath(), "MosWord-C7Fix2-" + Guid.NewGuid().ToString("N"));
        var logs = new CapturedLogs();
        var baseFactory = new ServerFactory(connection, new() { ["Packages:StorageRoot"] = root });
        var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        using var scope = factory.Services.CreateScope();
        Assert.Equal(28, await scope.ServiceProvider.GetRequiredService<PackageImporter>().ImportAsync(ProjectsRoot()));
        await using var db = PostgresFixture.Open(connection);
        return new Scenario { Connection = connection, Root = root, BaseFactory = baseFactory, Factory = factory,
            Client = client, Packages = await db.Packages.OrderBy(p => p.Code).ToListAsync(), Logs = logs };
    }

    private static List<TaskDefinition> Tasks(Package package) => JsonConvert.DeserializeObject<List<TaskDefinition>>(package.TasksJson)!;
    private static ExamDefinition Definition(Scenario s, Package target, IReadOnlyList<TaskDefinition> selected)
    {
        var projects = new List<ExamProjectDefinition> { new(target.Code, selected) };
        projects.AddRange(s.Packages.Where(p => p.Code != target.Code && Tasks(p).Count >= 5).Take(6)
            .Select(p => new ExamProjectDefinition(p.Code, Tasks(p).Take(5))));
        return new ExamDefinition(123, projects);
    }
    private static ExamSnapshot Snapshot(Scenario s, ExamDefinition definition) => new(
        s.Packages.Where(p => definition.Projects.Any(d => d.ProjectId == p.Code)).Select(p => p.Id).ToList(), [], [], JObject.FromObject(definition));
    private static byte[] DictionaryBytes(Package package, string language) =>
        File.ReadAllBytes(Path.Combine(ProjectsRoot(), package.Code, "lang", language + ".json"));
    private static JObject ExpectedInstructions(Package package, string language) =>
        JObject.Parse(File.ReadAllText(Path.Combine(ProjectsRoot(), package.Code, "lang", language + ".json")));

    [Fact]
    public async Task EveryRealPackageTaskResolvesBothLanguagesThroughWrongTasksPath()
    {
        await using var s = await ImportRealPackagesAsync();
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal(28, s.Packages.Count);
        Assert.Equal(22, s.Packages.Count(p => DictionaryBytes(p, "vi").AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })));
        int checkedInstructions = 0;
        foreach (var package in s.Packages)
        foreach (string language in new[] { "vi", "en" })
        {
            var tasks = Tasks(package);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chunk in tasks.Chunk(4))
            {
                var selected = chunk.Concat(tasks).DistinctBy(t => t.TaskId).Take(4).ToArray();
                var definition = Definition(s, package, selected);
                var score = new ExamScorer(_ => { }).Score(definition, definition.Projects.SelectMany(p => p.TaskIds.Select(id =>
                    new ExamTaskGrade(p.ProjectId, id, new TaskGradeResult(p.ProjectId == package.Code ? TaskGradeOutcome.Fail : TaskGradeOutcome.Pass, "", "", id)))));
                var validated = new ValidatedResult(score, DateTimeOffset.UtcNow, 0, false, language, definition, Snapshot(s, definition));
                var actual = JArray.Parse(await ResultValidation.WrongTasksAsync(validated, db, s.Storage, default));
                var expected = ExpectedInstructions(package, language);
                foreach (var wrong in actual)
                {
                    string key = (string)wrong["instructionKey"]!;
                    Assert.Equal((string?)expected[key], (string?)wrong["instruction"]);
                    Assert.Equal(package.Code, (string?)wrong["projectId"]);
                    seen.Add((string)wrong["taskId"]!);
                }
            }
            Assert.Equal(tasks.Select(t => t.TaskId).Order().ToArray(), seen.Order().ToArray());
            checkedInstructions += seen.Count;
        }
        Assert.Equal(s.Packages.Sum(p => Tasks(p).Count) * 2, checkedInstructions);
    }

    private static async Task<(Session Session, JObject Payload, string Token)> PrepareResultAsync(Scenario s, Package target)
    {
        var definition = Definition(s, target, Tasks(target).Take(4).ToArray());
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var account = new Account { Username = "bom-learner", NormalizedUsername = "BOM-LEARNER", MaxUses = 2, Used = 1 };
        var session = new Session { AccountId = account.Id, AccountCredentialVersion = account.CredentialVersion,
            LoginRequestId = Guid.NewGuid(), MachineId = new string('a', 64), Mode = "Testing", StartedAt = now,
            ModeSelectedAt = now, LastHeartbeatAt = now, ExamSeed = definition.Seed,
            ExamDefinitionHash = ExamDefinitionFingerprint.Compute(definition), ExamSnapshotJson = JsonConvert.SerializeObject(Snapshot(s, definition)) };
        await using (var db = PostgresFixture.Open(s.Connection)) { db.AddRange(account, session); await db.SaveChangesAsync(); }
        var payload = new JObject { ["version"] = 1, ["sessionId"] = session.Id, ["seed"] = definition.Seed,
            ["machine"] = session.MachineId, ["language"] = "vi", ["definition"] = JObject.FromObject(definition),
            ["startedUtc"] = now.ToString("O"), ["completedUtc"] = now.ToString("O"), ["elapsedTicks"] = 0L,
            ["clockRollbackDetected"] = false, ["pendingUpload"] = true, ["total"] = -999, ["passed"] = false,
            ["tasks"] = new JArray(definition.Projects.SelectMany(p => p.TaskIds.Select(id => new JObject
            { ["projectId"] = p.ProjectId, ["taskId"] = id, ["instructionKey"] = p.InstructionKeys[id],
                ["outcome"] = p.ProjectId == target.Code ? "Fail" : "Pass", ["points"] = -1, ["awardedPoints"] = -1 }))) };
        return (session, payload, s.Factory.Services.GetRequiredService<SessionTokens>().IssueUpload(session.Id, account.CredentialVersion));
    }
    private static Task<HttpResponseMessage> SendAsync(Scenario s, Session session, string token, byte[] bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/results") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Result-Signature", Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(session.ResultSigningKey), bytes)));
        return s.Client.SendAsync(request);
    }

    [Fact]
    public async Task RealBomVietnameseWrongTasksAcceptAndPersistWithIdempotentExactByteReceipt()
    {
        await using var s = await ImportRealPackagesAsync();
        var target = s.Packages.First(p => DictionaryBytes(p, "vi").AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        var (session, payload, token) = await PrepareResultAsync(s, target);
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
        using var first = await SendAsync(s, session, token, bytes);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = JObject.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(session.Id, (Guid)receipt["sessionId"]!); Assert.Equal(PackageStorage.Hash(bytes), (string?)receipt["payloadHash"]);
        await using var db = PostgresFixture.Open(s.Connection);
        var persistedBeforeRetry = await db.Results.AsNoTracking().SingleAsync();
        using var retry = await SendAsync(s, session, token, bytes);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var retryReceipt = JObject.Parse(await retry.Content.ReadAsStringAsync());
        receipt.Remove("serverTime"); retryReceipt.Remove("serverTime");
        Assert.True(JToken.DeepEquals(receipt, retryReceipt));
        using var bomResult = await SendAsync(s, session, token, [0xef, 0xbb, 0xbf, .. bytes]);
        Assert.Equal(HttpStatusCode.BadRequest, bomResult.StatusCode);
        var result = Assert.Single(await db.Results.ToListAsync());
        Assert.Equal(persistedBeforeRetry.ReceivedAt, result.ReceivedAt);
        Assert.Equal(persistedBeforeRetry.PayloadHash, result.PayloadHash);
        Assert.Equal(persistedBeforeRetry.PayloadJson, result.PayloadJson);
        Assert.Single(await db.AuditLog.Where(a => a.Action == "ResultAccepted").ToListAsync());
        var wrongTasks = JArray.Parse(result.WrongTasksJson); Assert.Equal(4, wrongTasks.Count);
        var expected = ExpectedInstructions(target, "vi");
        foreach (var wrong in wrongTasks) Assert.Equal((string?)expected[(string)wrong["instructionKey"]!], (string?)wrong["instruction"]);
        Assert.Equal(PackageStorage.Hash(bytes), result.PayloadHash);
        Assert.DoesNotContain(token, string.Join("", s.Logs.Lines));
        Assert.DoesNotContain(session.ResultSigningKey, string.Join("", s.Logs.Lines));
    }

    [Theory]
    [InlineData("missing-instruction", "InvalidDataException")]
    [InlineData("malformed-json", "JsonReaderException")]
    [InlineData("invalid-utf8", "DecoderFallbackException")]
    [InlineData("instruction-object", "InvalidDataException")]
    [InlineData("instruction-array", "InvalidDataException")]
    [InlineData("instruction-number", "InvalidDataException")]
    [InlineData("instruction-boolean", "InvalidDataException")]
    public async Task UnavailableInstructionsReturn503AndLogOnlySafeContext(string kind, string exceptionType)
    {
        await using var s = await ImportRealPackagesAsync();
        var target = s.Packages.First();
        var (session, payload, token) = await PrepareResultAsync(s, target);
        const string sentinel = "PRIVATE-INSTRUCTION-SENTINEL";
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("lang/vi.json");
            using var output = entry.Open();
            byte[] contents = kind switch
            {
                "missing-instruction" => Encoding.UTF8.GetBytes("{\"unrelated\":\"" + sentinel + "\"}"),
                "malformed-json" => Encoding.UTF8.GetBytes("{" + sentinel),
                "invalid-utf8" => [0x7b, 0x22, 0xc3, 0x28, 0x22, 0x3a, 0x30, 0x7d],
                "instruction-object" or "instruction-array" or "instruction-number" or "instruction-boolean" =>
                    Encoding.UTF8.GetBytes(new JObject(Tasks(target).Select(task => new JProperty(task.InstructionKey,
                        JToken.Parse(kind switch
                        {
                            "instruction-object" => "{\"value\":\"" + sentinel + "\"}",
                            "instruction-array" => "[\"" + sentinel + "\"]",
                            "instruction-number" => "42",
                            _ => "true"
                        })))).ToString(Formatting.None)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            output.Write(contents);
        }
        var broken = new Package { Code = target.Code, Version = "isolated-broken-instruction",
            ZipSha256 = PackageStorage.Hash(zip.ToArray()) };
        s.Storage.Store(broken, zip.ToArray());
        await using (var db = PostgresFixture.Open(s.Connection))
        {
            db.Packages.Add(broken);
            var storedSession = await db.Sessions.SingleAsync();
            var snapshot = JsonConvert.DeserializeObject<ExamSnapshot>(storedSession.ExamSnapshotJson!)!;
            snapshot.PackageIds.Remove(target.Id); snapshot.PackageIds.Add(broken.Id);
            storedSession.ExamSnapshotJson = JsonConvert.SerializeObject(snapshot);
            await db.SaveChangesAsync();
        }
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
        using var response = await SendAsync(s, session, token, bytes);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("pinned_instructions_unavailable", (string?)JObject.Parse(await response.Content.ReadAsStringAsync())["error"]);
        await using var verify = PostgresFixture.Open(s.Connection);
        Assert.Empty(await verify.Results.ToListAsync());
        string logs = string.Join("\n", s.Logs.Lines);
        Assert.Contains(session.Id.ToString(), logs); Assert.Contains(target.Code, logs);
        Assert.Contains("vi", logs); Assert.Contains(exceptionType, logs);
        Assert.DoesNotContain(token, logs); Assert.DoesNotContain(session.ResultSigningKey, logs);
        Assert.DoesNotContain(sentinel, logs); Assert.DoesNotContain(payload.ToString(Formatting.None), logs);
        Assert.Contains(s.Logs.States, state => state.TryGetValue("SessionId", out var loggedSession) &&
            loggedSession?.ToString() == session.Id.ToString() && state.TryGetValue("PackageCode", out var code) &&
            code?.ToString() == target.Code && state.TryGetValue("Language", out var language) && language?.ToString() == "vi" &&
            state.TryGetValue("ExceptionType", out var type) && type?.ToString() == exceptionType);
    }
}
