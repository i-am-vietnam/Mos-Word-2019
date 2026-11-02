using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class ResultsAdminTests(PostgresFixture postgres)
{
    private const string Password = "admin-results-password-123";
    private async Task<(string Connection, Guid AccountId, Guid ResultId)> SeedAsync(int count = 1)
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.Open(connection);
        await AdminBootstrap.CreateFirstAsync(db, new PasswordHasher<Account>(), "owner", Password);
        var account = new Account { Username = "results-learner", NormalizedUsername = "RESULTS-LEARNER" };
        var other = new Account { Username = "other-learner", NormalizedUsername = "OTHER-LEARNER" };
        db.Accounts.AddRange(account, other);
        Guid resultId = default;
        for (var i = 0; i < count + 1; i++)
        {
            var session = new Session { AccountId = i == count ? other.Id : account.Id,
                MachineId = new string('a', 64), LoginRequestId = Guid.NewGuid(), Mode = "Testing" };
            db.Sessions.Add(session);
            var result = new Result { SessionId = session.Id, ReceivedAt = DateTimeOffset.Parse("2026-11-02T12:00:00Z").AddMinutes(-i),
                PayloadHash = new string('a', 64), Score = i == 0 ? 700 : 699, Passed = i == 0,
                FlagsJson = "[\"ClientClockRollback\",\"TimingInconsistent\"]",
                WrongTasksJson = JsonSerializer.Serialize(new[] { new { projectId = "Word2019_P01", taskId = "T01",
                    instructionKey = "p01.t01", instruction = "<script>alert('instruction')</script>" } }),
                OfflineGapsJson = "[{\"kind\":\"Network\",\"startedAt\":\"2026-11-02T11:00:00Z\",\"endedAt\":\"2026-11-02T11:01:00Z\"}]" };
            db.Results.Add(result);
            if (i == 0) resultId = result.Id;
        }
        await db.SaveChangesAsync();
        return (connection, account.Id, resultId);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/Admin/Login");
        var token = WebUtility.HtmlDecode(Regex.Match(login,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["Username"] = "owner", ["Password"] = Password,
                ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task ResultPagesRequireAdminAndDetailEncodesInstructions()
    {
        var (connection, accountId, resultId) = await SeedAsync();
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        foreach (var path in new[] { "/Admin/Results", $"/Admin/Results/Details/{resultId}" })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/Admin/Login", denied.Headers.Location!.ToString());
        }
        await SignInAsync(client);
        var detail = await client.GetStringAsync($"/Admin/Results/Details/{resultId}");
        Assert.Contains("700 / 1000", detail);
        Assert.Contains("Đạt", WebUtility.HtmlDecode(detail));
        Assert.Contains("Word2019_P01", detail);
        Assert.Contains("p01.t01", detail);
        Assert.Contains("&lt;script&gt;", detail);
        Assert.DoesNotContain("<script>", detail);
        Assert.Contains("ClientClockRollback", detail);
        Assert.Contains("TimingInconsistent", detail);
        Assert.Contains("Network", detail);
        Assert.Contains(accountId.ToString(), detail);
        using var missing = await client.GetAsync($"/Admin/Results/Details/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ResultListFiltersAccountUtcDatesAndBoundsPagination()
    {
        var (connection, accountId, _) = await SeedAsync(51);
        await using var factory = new ServerFactory(connection);
        using var client = factory.CreateHttpsClient();
        await SignInAsync(client);
        var list = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Results"));
        Assert.Equal(50, Regex.Matches(list, "/Admin/Results/Details/").Count);
        Assert.Contains("700 / 1000", list);
        Assert.Contains("Cần Luyện Tập thêm", list);
        var second = await client.GetStringAsync($"/Admin/Results?accountId={accountId}&pageNumber=2&fromDate=2026-11-02&toDate=2026-11-02");
        Assert.Single(Regex.Matches(second, "/Admin/Results/Details/").Cast<Match>());
        Assert.DoesNotContain("other-learner", second);
        var empty = await client.GetStringAsync($"/Admin/Results?accountId={accountId}&toDate=2026-11-01");
        Assert.DoesNotContain("/Admin/Results/Details/", empty);
        foreach (var query in new[] { "pageNumber=0", "pageNumber=1000001", "pageNumber=abc", "accountId=bad",
                     "fromDate=bad", "fromDate=2026-11-03&toDate=2026-11-02", "toDate=9999-12-31" })
        {
            using var invalid = await client.GetAsync("/Admin/Results?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var missing = await client.GetAsync($"/Admin/Results?accountId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("/Admin/Results", await client.GetStringAsync("/Admin"));
        Assert.Contains("/Admin/Results", await client.GetStringAsync("/Admin/Accounts"));
    }
}
