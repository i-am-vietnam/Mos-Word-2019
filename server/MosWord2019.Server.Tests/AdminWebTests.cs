using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AdminWebTests(PostgresFixture postgres)
{
    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/Admin/Login");
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "The login form must include an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string token, string password,
        string username = "owner", string path = "/Admin/Login") => client.PostAsync(path, new FormUrlEncodedContent(
        new Dictionary<string, string> { ["Username"] = username, ["Password"] = password, ["__RequestVerificationToken"] = token }));

    private async Task<(string Connection, string Password)> CreateAdminAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var password = Guid.NewGuid().ToString("N");
        await using var db = PostgresFixture.Open(connection);
        await AdminBootstrap.CreateFirstAsync(db, new PasswordHasher<Account>(), "owner", password);
        return (connection, password);
    }

    [Fact]
    public async Task AnonymousAdminAccessChallengesAndMissingCsrfIsRejected()
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateHttpsClient();
        using var index = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, index.StatusCode);
        Assert.Contains("/Admin/Login", index.Headers.Location!.ToString());
        using var post = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["Username"] = "owner", ["Password"] = "invalid" }));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task AdminLoginSetsSecureCookieDoesNotConsumeUseAndLogoutRequiresCsrf()
    {
        var (connection, password) = await CreateAdminAsync();
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        using var response = await SignInAsync(client, await AntiforgeryTokenAsync(client), password, "OWNER");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-MosWordAdmin="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        var index = await client.GetStringAsync("/Admin");
        Assert.Contains("Signed in as owner", index);
        using var getLogout = await client.GetAsync("/Admin/Logout");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getLogout.StatusCode);
        using var badLogout = await client.PostAsync("/Admin/Logout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, badLogout.StatusCode);
        var logoutToken = WebUtility.HtmlDecode(Regex.Match(index,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var logout = await client.PostAsync("/Admin/Logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = logoutToken }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using var denied = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        await using var db = PostgresFixture.Open(connection);
        Assert.Equal(0, (await db.Accounts.SingleAsync()).Used);
        Assert.Empty(await db.Sessions.ToListAsync());
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("unknown")]
    [InlineData("locked")]
    [InlineData("expired")]
    [InlineData("non-admin")]
    public async Task InvalidCredentialsAndDisabledAdminsCannotSignIn(string scenario)
    {
        var (connection, password) = await CreateAdminAsync();
        await using (var db = PostgresFixture.Open(connection))
        {
            var account = await db.Accounts.SingleAsync();
            if (scenario == "locked") account.Locked = true;
            if (scenario == "expired") account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (scenario == "non-admin") account.IsAdmin = false;
            await db.SaveChangesAsync();
        }
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        using var response = await SignInAsync(client, await AntiforgeryTokenAsync(client),
            scenario == "wrong-password" ? "incorrect" : password, scenario == "unknown" ? "missing" : "owner");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid administrator credentials.", body);
        Assert.DoesNotContain(password, body);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("__Host-MosWordAdmin=")));
        using var denied = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("expired")]
    [InlineData("non-admin")]
    public async Task ExistingCookieIsRejectedWhenAdminAccessIsRevoked(string scenario)
    {
        var (connection, password) = await CreateAdminAsync();
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        using var login = await SignInAsync(client, await AntiforgeryTokenAsync(client), password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        await using (var db = PostgresFixture.Open(connection))
        {
            var account = await db.Accounts.SingleAsync();
            if (scenario == "locked") account.Locked = true;
            if (scenario == "expired") account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (scenario == "non-admin") account.IsAdmin = false;
            await db.SaveChangesAsync();
        }
        using var denied = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
    }

    [Theory]
    [InlineData("/Admin/Login")]
    [InlineData("/Admin/Login/")]
    [InlineData("/admin/login")]
    public async Task LoginAndAdminPagesAreRateLimitedButHealthRemainsAvailable(string loginPath)
    {
        await using var factory = new ServerFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateHttpsClient();
        var token = await AntiforgeryTokenAsync(client);
        for (var i = 0; i < 5; i++)
        {
            using var response = await SignInAsync(client, token, "wrong", path: loginPath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var rejected = await SignInAsync(client, token, "wrong", path: loginPath);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        for (var i = 0; i < 60; i++) { using var ignored = await client.GetAsync("/Admin"); }
        using var adminRejected = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.TooManyRequests, adminRejected.StatusCode);
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ReadinessRequiresAppliedMigration()
    {
        var connection = await postgres.CreateDatabaseAsync(migrate: false);
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        using var before = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);
        await using (var db = PostgresFixture.Open(connection)) await db.Database.MigrateAsync();
        using var after = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }
}
