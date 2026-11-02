using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MosWord2019.Server.Data;
using MosWord2019.Server.Packages;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class PackageApiTests(PostgresFixture postgres)
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required ServerFactory Factory;
        public required HttpClient Client;
        public required string Connection;
        public required string Root;
        public required Clock Time;
        public required byte[] PublicKey;
        public JsonElement Login;
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await Factory.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }

    private async Task<Scenario> StartAsync(string? mode = "Training")
    {
        string connection = await postgres.CreateDatabaseAsync();
        string root = Path.Combine(Path.GetTempPath(), "MosWord-package-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] signingKey = RandomNumberGenerator.GetBytes(32);
        string signingPath = Path.Combine(root, "manifest.key");
        await File.WriteAllBytesAsync(signingPath, signingKey);
        var clock = new Clock();
        var factory = new ServerFactory(connection, new()
        { ["Packages:StorageRoot"] = Path.Combine(root, "storage"), ["Packages:SigningKeyPath"] = signingPath }, clock, true);
        using (var scope = factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<PackageStorage>();
            await using var db = PostgresFixture.Open(connection);
            var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", MaxUses = 10 };
            account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, "Test-Only-Password-123!");
            db.Accounts.Add(account);
            for (int i = 1; i <= 8; i++)
            {
                string code = "Word2019_P" + i.ToString("D2");
                byte[] zip;
                using (var buffer = new MemoryStream())
                {
                    using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                    using (var writer = new StreamWriter(archive.CreateEntry("starter.docx").Open())) writer.Write("test starter");
                    zip = buffer.ToArray();
                }
                string hash = Convert.ToHexStringLower(SHA256.HashData(zip));
                var package = new Package { Code = code, Version = hash, ZipSha256 = hash, CreatedAt = clock.Now,
                    MetadataJson = JsonSerializer.Serialize(new { ProjectId = code, Starter = "starter.docx" }),
                    TasksJson = JsonSerializer.Serialize(Enumerable.Range(1, 6).Select(t => new { TaskId = "T" + t, InstructionKey = "task." + t })),
                    CatalogEntryJson = JsonSerializer.Serialize(new { projectId = code, eligible = true, requiredTaskIds = new string[0],
                        excludedTaskIds = new string[0], taskGroups = new string[0][], notes = "Test fixture" }) };
                storage.Store(package, zip); db.Packages.Add(package);
            }
            await db.SaveChangesAsync();
        }
        var client = factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "learner", password = "Test-Only-Password-123!",
            machineId = new string('a', 64), idempotencyKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<JsonElement>();
        var scenario = new Scenario { Factory = factory, Client = client, Connection = connection, Root = root,
            Time = clock, PublicKey = new Ed25519PrivateKeyParameters(signingKey, 0).GeneratePublicKey().GetEncoded(), Login = login };
        if (mode != null)
        {
            using var select = await SendAsync(scenario, HttpMethod.Post, "/api/sessions/mode", new { mode });
            Assert.Equal(HttpStatusCode.OK, select.StatusCode);
            scenario.Login = await select.Content.ReadFromJsonAsync<JsonElement>();
        }
        return scenario;
    }

    private static Task<HttpResponseMessage> SendAsync(Scenario s, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Login.GetProperty("sessionToken").GetString());
        if (body != null) request.Content = JsonContent.Create(body);
        return s.Client.SendAsync(request);
    }

    private static async Task<JsonElement> ManifestAsync(Scenario s)
    {
        using var response = await SendAsync(s, HttpMethod.Get, "/api/packages/manifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Session-Token"));
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        byte[] payload = Convert.FromBase64String(envelope.GetProperty("payload").GetString()!);
        var verifier = new Ed25519Signer(); verifier.Init(false, new Ed25519PublicKeyParameters(s.PublicKey, 0));
        verifier.BlockUpdate(payload, 0, payload.Length);
        Assert.True(verifier.VerifySignature(Convert.FromBase64String(envelope.GetProperty("signature").GetString()!)));
        return JsonSerializer.Deserialize<JsonElement>(payload);
    }

    [Fact]
    public async Task TrainingManifestDownloadsEncryptedZipAndAuditsSession()
    {
        await using var s = await StartAsync();
        var manifest = await ManifestAsync(s);
        Assert.Equal(8, manifest.GetProperty("packages").GetArrayLength());
        var p = manifest.GetProperty("packages")[0];
        using var download = await SendAsync(s, HttpMethod.Get, p.GetProperty("downloadPath").GetString()!);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.True(download.Headers.Contains("Session-Token"));
        byte[] blob = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal("MWPKG001", Encoding.ASCII.GetString(blob, 0, 8));
        byte[] zip = new byte[blob.Length - 36];
        using var aes = new AesGcm(Convert.FromBase64String(p.GetProperty("key").GetString()!), 16);
        aes.Decrypt(blob.AsSpan(8, 12), blob.AsSpan(36), blob.AsSpan(20, 16), zip,
            Encoding.UTF8.GetBytes("MosWord2019.Package.v1|" + p.GetProperty("code").GetString() + "|" + p.GetProperty("version").GetString()));
        Assert.Equal(p.GetProperty("zipSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(zip)));
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Contains(await db.AuditLog.Where(a => a.Action == "PackageDownloaded").ToListAsync(),
            a => a.DetailsJson.Contains(s.Login.GetProperty("sessionId").GetGuid().ToString()));
    }

    [Fact]
    public async Task TestingPinsSevenPackagesAndRequiresMatchingDefinitionReport()
    {
        await using var s = await StartAsync("Testing");
        var manifest = await ManifestAsync(s);
        Assert.Equal(7, manifest.GetProperty("packages").GetArrayLength());
        Assert.Equal(8, manifest.GetProperty("examPool").GetArrayLength());
        var firstPath = manifest.GetProperty("packages")[0].GetProperty("downloadPath").GetString()!;
        using var unreported = await SendAsync(s, HttpMethod.Get, firstPath);
        Assert.Equal(HttpStatusCode.Conflict, unreported.StatusCode);
        using var mismatch = await SendAsync(s, HttpMethod.Post, "/api/sessions/definition", new { definitionHash = new string('f', 64) });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        using var report = await SendAsync(s, HttpMethod.Post, "/api/sessions/definition", new { definitionHash = manifest.GetProperty("definitionHash").GetString() });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        using var allowed = await SendAsync(s, HttpMethod.Get, firstPath);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await using var db = PostgresFixture.Open(s.Connection);
        var permitted = manifest.GetProperty("packages").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToHashSet();
        Guid other = await db.Packages.Where(p => !permitted.Contains(p.Id)).Select(p => p.Id).SingleAsync();
        using var denied = await SendAsync(s, HttpMethod.Get, "/api/packages/" + other + "/download");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var stored = await db.Sessions.SingleAsync();
        Assert.NotNull(stored.ExamSeed); Assert.NotNull(stored.ModeSelectedAt);
        Assert.Equal(stored.ExamDefinitionHash, stored.ReportedDefinitionHash);
    }

    [Theory]
    [InlineData(null, 16, "Expired")]
    [InlineData("Training", 180, "Abandoned")]
    [InlineData("Training", 241, "TimeLimit")]
    [InlineData("Testing", 121, "TimeLimit")]
    public async Task AbandonedClientCannotDownloadAndIsEndedOnce(string? mode, int minutes, string reason)
    {
        await using var s = await StartAsync(mode);
        await using var db = PostgresFixture.Open(s.Connection);
        var id = await db.Packages.Select(p => p.Id).FirstAsync();
        s.Time.Now = s.Time.Now.AddMinutes(minutes);
        for (int i = 0; i < 2; i++)
        {
            using var response = await SendAsync(s, HttpMethod.Get, "/api/packages/" + id + "/download");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        var session = await db.Sessions.AsNoTracking().SingleAsync();
        Assert.NotNull(session.EndedAt); Assert.Equal(reason, session.EndReason);
        Assert.Equal(1, await db.AuditLog.CountAsync(a => a.Action == "SessionEnded"));
        Assert.Equal(0, await db.AuditLog.CountAsync(a => a.Action == "PackageDownloaded"));
    }

    [Fact]
    public async Task RealRepositoryImportIsIdempotentAndRefreshesCatalogWithoutChangingPinnedExam()
    {
        await using var s = await StartAsync("Testing");
        string source = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(source, ".git"))) source = Path.GetDirectoryName(source) ?? throw new InvalidOperationException("Repository not found.");
        source = Path.Combine(source, "MosWord2019.WinForms", "Projects");
        s.Time.Now = s.Time.Now.AddSeconds(1);
        using var scope = s.Factory.Services.CreateScope();
        using var parallelScope = s.Factory.Services.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<PackageImporter>();
        var concurrent = await Task.WhenAll(importer.ImportAsync(source),
            parallelScope.ServiceProvider.GetRequiredService<PackageImporter>().ImportAsync(source));
        Assert.Equal(new[] { 0, 28 }, concurrent.Order().ToArray());
        Assert.Equal(0, await importer.ImportAsync(source));
        await using var db = PostgresFixture.Open(s.Connection);
        Assert.Equal(36, await db.Packages.CountAsync());
        var latest = await ServerExamPool.LatestAsync(db, default);
        Assert.Equal(28, latest.Count);
        foreach (var package in latest)
        {
            Assert.Equal(package.Version, package.ZipSha256);
            Assert.True(File.Exists(Path.Combine(s.Root, "storage", package.StoragePath)));
        }
        string copy = Path.Combine(s.Root, "source-copy");
        foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(copy, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
        }
        string catalogPath = Path.Combine(copy, "testing-catalog.json");
        var catalog = Newtonsoft.Json.Linq.JArray.Parse(await File.ReadAllTextAsync(catalogPath));
        catalog[0]["notes"] = "Updated catalog only";
        await File.WriteAllTextAsync(catalogPath, catalog.ToString());
        Assert.Equal(0, await importer.ImportAsync(copy));
        Assert.Contains("Updated catalog only", (await ServerExamPool.LatestAsync(db, default)).Single(p => p.Code == "Word2019_P01").CatalogEntryJson);
        Assert.Equal(72, Directory.GetFiles(Path.Combine(s.Root, "storage")).Length);
        // A real source import cannot change the seven immutable versions pinned to this earlier exam.
        var manifest = await ManifestAsync(s);
        Assert.Equal(7, manifest.GetProperty("packages").GetArrayLength());
        Assert.Equal(8, manifest.GetProperty("examPool").GetArrayLength());
    }

    [Fact]
    public async Task PendingModeCannotGetManifestAndUnauthenticatedCannotDownload()
    {
        await using var s = await StartAsync(null);
        using var pending = await SendAsync(s, HttpMethod.Get, "/api/packages/manifest");
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        using var anonymous = await s.Client.GetAsync("/api/packages/manifest");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
