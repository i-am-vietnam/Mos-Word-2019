using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class SecurityInfrastructureTests(PostgresFixture postgres)
{
    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
    private static async Task<int> SendAsync(ServerFactory factory, string path, object body,
        IPAddress remote, string forwardedIp, string? token = null, string scheme = "http")
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        using var stream = new MemoryStream(bytes);
        var response = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = remote;
            context.Request.Method = "POST";
            context.Request.Scheme = scheme;
            context.Request.Host = new HostString("localhost");
            context.Request.Path = path;
            context.Request.Headers["X-Forwarded-For"] = forwardedIp;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            if (token != null) context.Request.Headers.Authorization = "Bearer " + token;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = stream;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
        });
        return response.Response.StatusCode;
    }

    private static object InvalidLogin(string username) => new { username, password = "intentionally-invalid",
        machineId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username))).ToLowerInvariant(),
        idempotencyKey = Guid.NewGuid() };

    [Fact]
    public async Task ForwardedHeadersTrustOnlyLoopbackAndAcceptOneHop()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateHttpsClient();
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(1, options.ForwardLimit);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Equal(2, options.KnownProxies.Count);
        Assert.Contains(IPAddress.Loopback, options.KnownProxies);
        Assert.Contains(IPAddress.IPv6Loopback, options.KnownProxies);
    }

    [Fact]
    public async Task TrustedProxyCreatesIndependentLoginIpBuckets()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateHttpsClient();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(401, await SendAsync(factory, "/api/auth/login", InvalidLogin("first" + i),
                IPAddress.Loopback, "198.51.100.1"));
            Assert.Equal(401, await SendAsync(factory, "/api/auth/login", InvalidLogin("second" + i),
                IPAddress.Loopback, "198.51.100.2"));
        }
        Assert.Equal(429, await SendAsync(factory, "/api/auth/login", InvalidLogin("firstover"),
            IPAddress.Loopback, "198.51.100.1"));
        Assert.Equal(429, await SendAsync(factory, "/api/auth/login", InvalidLogin("secondover"),
            IPAddress.Loopback, "198.51.100.2"));
    }

    [Fact]
    public async Task UntrustedPeerCannotEscapeIpBucketWithForgedForwardedAddresses()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateHttpsClient();
        for (var i = 0; i < 10; i++)
            Assert.Equal(401, await SendAsync(factory, "/api/auth/login", InvalidLogin("spoof" + i),
                IPAddress.Parse("203.0.113.1"), "198.51.100." + (i + 1), scheme: "https"));
        Assert.Equal(429, await SendAsync(factory, "/api/auth/login", InvalidLogin("spoofextra"),
            IPAddress.Parse("203.0.113.1"), "198.51.100.200", scheme: "https"));
    }

    [Fact]
    public async Task UsernameBucketSurvivesChangingTrustedForwardedIp()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateHttpsClient();
        for (var i = 0; i < 10; i++)
            Assert.Equal(401, await SendAsync(factory, "/api/auth/login", InvalidLogin(i % 2 == 0 ? "target" : "TARGET"),
                IPAddress.Loopback, "198.51.100." + (i + 1)));
        Assert.Equal(429, await SendAsync(factory, "/api/auth/login", InvalidLogin("target"),
            IPAddress.Loopback, "198.51.100.100"));
        Assert.Equal(401, await SendAsync(factory, "/api/auth/login", InvalidLogin("anotheruser"),
            IPAddress.Loopback, "198.51.100.101"));
    }

    [Fact]
    public async Task ApiSessionBucketsRemainSeparateAndCannotBeResetByChangingIp()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateHttpsClient();
        var tokens = factory.Services.GetRequiredService<SessionTokens>();
        var version = Guid.NewGuid();
        var first = tokens.Issue(Guid.NewGuid(), version);
        var second = tokens.Issue(Guid.NewGuid(), version);
        for (var i = 0; i < 60; i++)
        {
            Assert.Equal(400, await SendAsync(factory, "/api/sessions/mode", new { mode = "Invalid" },
                IPAddress.Loopback, "198.51.100." + (i + 1), first));
            Assert.Equal(400, await SendAsync(factory, "/api/sessions/mode", new { mode = "Invalid" },
                IPAddress.Loopback, "198.51.100." + (i + 1), second));
        }
        Assert.Equal(429, await SendAsync(factory, "/api/sessions/mode", new { mode = "Invalid" },
            IPAddress.Loopback, "198.51.100.200", first));
        Assert.Equal(429, await SendAsync(factory, "/api/sessions/mode", new { mode = "Invalid" },
            IPAddress.Loopback, "198.51.100.201", second));
    }

    [Fact]
    public async Task AccessAndResumeTokensShareOneSessionRequestBudget()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateHttpsClient();
        var tokens = factory.Services.GetRequiredService<SessionTokens>();
        var sessionId = Guid.NewGuid();
        var version = Guid.NewGuid();
        var access = tokens.Issue(sessionId, version);
        var resume = tokens.Issue(sessionId, version, true);
        for (var i = 0; i < 60; i++)
            Assert.Equal(400, await SendAsync(factory, "/api/sessions/mode", new { mode = "Invalid" },
                IPAddress.Loopback, "198.51.100." + (i + 1), access));
        Assert.Equal(429, await SendAsync(factory, "/api/sessions/resume", new
        {
            resumeToken = resume,
            machineId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        }, IPAddress.Loopback, "198.51.100.200"));
    }

    [Fact]
    public async Task ProductionStartupRequiresExplicitDataProtectionKeyPath()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        await using var production = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["DataProtection:KeyPath"] = null }));
        });
        var exception = Assert.Throws<InvalidOperationException>(() => production.CreateClient());
        Assert.Contains("DataProtection:KeyPath", exception.Message);
    }

    [Fact]
    public async Task ProductionStartupAcceptsExplicitExternalKeyPath()
    {
        var path = NewExternalKeyPath();
        try
        {
            await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
            await using var production = factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["DataProtection:KeyPath"] = path }));
            });
            using var client = production.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
                { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally { DeleteOwnKeyPath(path); }
    }

    [Fact]
    public async Task StartupRejectsKeyStorageInsideRepository()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        // The test output lives under server/.../bin inside this repository.
        var path = Path.Combine(AppContext.BaseDirectory, "must-not-create-keys");
        await using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["DataProtection:KeyPath"] = path })));
        var exception = Assert.Throws<InvalidOperationException>(() => configured.CreateClient());
        Assert.Contains("outside the repository", exception.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void PersistedKeyRingAllowsTokensAfterProviderRestartAndKeepsPurposesSeparate()
    {
        var path = NewExternalKeyPath();
        try
        {
            var identity = new SessionIdentity(Guid.NewGuid(), Guid.NewGuid());
            string access;
            string resume;
            var firstProvider = DataProtectionProvider.Create(new DirectoryInfo(path),
                builder => builder.SetApplicationName("MosWord2019.Server"));
            try
            {
                var first = new SessionTokens(firstProvider);
                access = first.Issue(identity.SessionId, identity.CredentialVersion);
                resume = first.Issue(identity.SessionId, identity.CredentialVersion, true);
            }
            finally { (firstProvider as IDisposable)?.Dispose(); }
            var restartedProvider = DataProtectionProvider.Create(new DirectoryInfo(path),
                builder => builder.SetApplicationName("MosWord2019.Server"));
            try
            {
                var restarted = new SessionTokens(restartedProvider);
                Assert.Equal(identity, restarted.Read(access));
                Assert.Equal(identity, restarted.Read(resume, true));
                Assert.Null(restarted.Read(access, true));
                Assert.Null(restarted.Read(resume));
                Assert.Null(restarted.Read("invalid-token"));
            }
            finally { (restartedProvider as IDisposable)?.Dispose(); }
            Assert.NotEmpty(Directory.GetFiles(path, "key-*.xml"));
        }
        finally { DeleteOwnKeyPath(path); }
    }

    private static string NewExternalKeyPath() => Path.Combine(Path.GetTempPath(), "MosWord2019-c1-keys-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ApplicationRestartRetainsLearnerTokenAndAdminCookieWithPersistedKeys()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var path = NewExternalKeyPath();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var db = PostgresFixture.Open(connection))
        {
            foreach (var admin in new[] { false, true })
            {
                var account = new Account { Username = admin ? "owner" : "learner",
                    NormalizedUsername = admin ? "OWNER" : "LEARNER", IsAdmin = admin, MaxUses = 2 };
                account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, password);
                db.Accounts.Add(account);
            }
            await db.SaveChangesAsync();
        }
        WebApplicationFactory<Program> Persistent(ServerFactory root) => root.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["DataProtection:KeyPath"] = path }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDataProtectionProvider>();
                services.AddDataProtection();
            });
        });
        HttpClient Client(WebApplicationFactory<Program> host) => host.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        try
        {
            string sessionToken;
            string cookie;
            await using (var root = new ServerFactory(connection))
            await using (var host = Persistent(root))
            using (var client = Client(host))
            {
                using var login = await client.PostAsJsonAsync("/api/auth/login", new
                { username = "learner", password, machineId = new string('a', 64), idempotencyKey = Guid.NewGuid() });
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                sessionToken = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionToken").GetString()!;
                var form = await client.GetStringAsync("/Admin/Login");
                var token = WebUtility.HtmlDecode(Regex.Match(form,
                    "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
                using var adminLogin = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["Username"] = "owner", ["Password"] = password, ["__RequestVerificationToken"] = token }));
                Assert.Equal(HttpStatusCode.Redirect, adminLogin.StatusCode);
                cookie = Assert.Single(adminLogin.Headers.GetValues("Set-Cookie"),
                    value => value.StartsWith("__Host-MosWordAdmin=", StringComparison.Ordinal)).Split(';')[0];
            }
            Assert.NotEmpty(Directory.GetFiles(path, "key-*.xml"));
            await using (var root = new ServerFactory(connection))
            await using (var restarted = Persistent(root))
            using (var client = Client(restarted))
            {
                client.DefaultRequestHeaders.Add("Cookie", cookie);
                using var admin = await client.GetAsync("/Admin/Accounts");
                Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/mode")
                { Content = JsonContent.Create(new { mode = "Testing" }) };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
                using var mode = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, mode.StatusCode);
            }
            await using var check = PostgresFixture.Open(connection);
            Assert.Equal(1, (await check.Accounts.SingleAsync(a => !a.IsAdmin)).Used);
        }
        finally { DeleteOwnKeyPath(path); }
    }

    private static void DeleteOwnKeyPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("MosWord2019-c1-keys-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a directory outside this test's temporary key storage.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }
}
