using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AccountPagesTests(PostgresFixture postgres)
{
    private const string Password = "admin-password-12345";
    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

    private async Task<string> SetupAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.Open(connection);
        await AdminBootstrap.CreateFirstAsync(db, new PasswordHasher<Account>(), "owner", Password);
        return connection;
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/Admin/Login");
        using var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Username"] = "owner", ["Password"] = Password, ["__RequestVerificationToken"] = Token(html) }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData("/Admin/Accounts")]
    [InlineData("/Admin/Accounts/Create")]
    [InlineData("/Admin/Accounts/Edit/00000000-0000-0000-0000-000000000001")]
    [InlineData("/Admin/Accounts/Sessions/00000000-0000-0000-0000-000000000001")]
    public async Task LearnerAdministrationRequiresAdminAuthentication(string path)
    {
        await using var factory = new ServerFactory(await SetupAsync());
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Admin/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task CreateRequiresCsrfValidatesWithoutEchoAndCannotPromoteLearner()
    {
        var connection = await SetupAsync();
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        await LoginAsync(client);
        using var noCsrf = await client.PostAsync("/Admin/Accounts/Create", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var html = await client.GetStringAsync("/Admin/Accounts/Create");
        var form = new Dictionary<string, string>
        {
            ["Username"] = "bad name", ["Password"] = "learner-password-123", ["MaxUses"] = "5",
            ["IsAdmin"] = "true", ["__RequestVerificationToken"] = Token(html)
        };
        using var invalid = await client.PostAsync("/Admin/Accounts/Create", new FormUrlEncodedContent(form));
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Username must contain", invalidHtml);
        Assert.DoesNotContain("learner-password-123", invalidHtml);
        form["Username"] = "learner";
        using var created = await client.PostAsync("/Admin/Accounts/Create", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        await using var db = PostgresFixture.Open(connection);
        var learner = await db.Accounts.SingleAsync(x => x.Username == "learner");
        Assert.False(learner.IsAdmin);
        Assert.Equal(5, learner.MaxUses);
        Assert.Contains("/Admin/Accounts/Edit/", created.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("            ")]
    public async Task EmptyOrWhitespaceCreatePasswordReturnsValidationWithoutCreatingLearner(string password)
    {
        var connection = await SetupAsync();
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        await LoginAsync(client);
        var html = await client.GetStringAsync("/Admin/Accounts/Create");
        using var response = await client.PostAsync("/Admin/Accounts/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "learner", ["Password"] = password, ["MaxUses"] = "5",
            ["__RequestVerificationToken"] = Token(html)
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Password must contain 12-128 characters and cannot be blank.", body);
        await using var db = PostgresFixture.Open(connection);
        Assert.False(await db.Accounts.AnyAsync(x => !x.IsAdmin));
        Assert.False(await db.AuditLog.AnyAsync(x => x.Action == "LearnerCreated"));
    }

    [Fact]
    public async Task EditRequiresCsrfRejectsAdminAndIgnoresPrivilegeAndUsageOverposting()
    {
        var connection = await SetupAsync();
        Guid id;
        Guid adminId;
        await using (var db = PostgresFixture.Open(connection))
        {
            adminId = (await db.Accounts.SingleAsync()).Id;
            id = (await new AccountAdministration(db, new PasswordHasher<Account>()).CreateAsync("learner", "learner-password-123", 4, null, false)).AccountId!.Value;
        }
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        await LoginAsync(client);
        using var adminEdit = await client.GetAsync($"/Admin/Accounts/Edit/{adminId}");
        Assert.Equal(HttpStatusCode.NotFound, adminEdit.StatusCode);
        using var noCsrf = await client.PostAsync($"/Admin/Accounts/Edit/{id}", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        var html = await client.GetStringAsync($"/Admin/Accounts/Edit/{id}");
        using var adminPost = await client.PostAsync($"/Admin/Accounts/Edit/{adminId}", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["AddUses"] = "3", ["Locked"] = "true", ["__RequestVerificationToken"] = Token(html) }));
        Assert.Equal(HttpStatusCode.NotFound, adminPost.StatusCode);
        using var saved = await client.PostAsync($"/Admin/Accounts/Edit/{id}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["AddUses"] = "3", ["Locked"] = "true", ["Used"] = "99", ["IsAdmin"] = "true",
            ["__RequestVerificationToken"] = Token(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await using var persisted = PostgresFixture.Open(connection);
        var learner = await persisted.Accounts.SingleAsync(x => x.Id == id);
        Assert.False(learner.IsAdmin);
        Assert.True(learner.Locked);
        Assert.Equal(0, learner.Used);
        Assert.Equal(7, learner.MaxUses);
        var admin = await persisted.Accounts.SingleAsync(x => x.Id == adminId);
        Assert.True(admin.IsAdmin);
        Assert.False(admin.Locked);
        Assert.Equal(0, admin.MaxUses);
    }

    [Fact]
    public async Task SearchPaginatesLearnersAndSessionsExposeOnlyMachinePrefix()
    {
        var connection = await SetupAsync();
        Guid id;
        var machine = new string('a', 64);
        await using (var db = PostgresFixture.Open(connection))
        {
            for (var i = 0; i < 51; i++) db.Accounts.Add(new Account
            { Username = $"learner{i:D2}", NormalizedUsername = $"LEARNER{i:D2}", PasswordHash = "unused" });
            await db.SaveChangesAsync();
            id = (await db.Accounts.SingleAsync(x => x.Username == "learner00")).Id;
            db.Sessions.Add(new Session { AccountId = id, LoginRequestId = Guid.NewGuid(), MachineId = machine, Mode = "Training" });
            await db.SaveChangesAsync();
        }
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        await LoginAsync(client);
        var html = await client.GetStringAsync("/Admin/Accounts");
        Assert.Contains("learner49", html);
        Assert.DoesNotContain("learner50", html);
        Assert.Contains(">Next</a>", html);
        var next = await client.GetStringAsync("/Admin/Accounts?PageNumber=2");
        Assert.Contains("learner50", next);
        Assert.DoesNotContain("learner49", next);
        var search = await client.GetStringAsync("/Admin/Accounts?Search=LEARNER50");
        Assert.Contains("learner50", search);
        Assert.DoesNotContain("learner49", search);
        var sessions = await client.GetStringAsync($"/Admin/Accounts/Sessions/{id}");
        Assert.Contains(id.ToString(), sessions);
        Assert.Contains("Training", sessions);
        Assert.Contains(new string('a', 12), sessions);
        Assert.DoesNotContain(machine, sessions);
        Assert.DoesNotContain("PasswordHash", sessions);
        Assert.DoesNotContain("CredentialVersion", sessions);
    }
}
