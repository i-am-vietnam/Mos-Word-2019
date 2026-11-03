using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Pages.Admin;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AdminDashboardTests(PostgresFixture postgres)
{
    private const string Password = "dashboard-password-123";
    private sealed class Clock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-03T00:30:00Z"); }
    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private async Task<(string Connection, Guid Id)> SeedAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.Open(connection);
        await AdminBootstrap.CreateFirstAsync(db, new PasswordHasher<Account>(), "owner", Password);
        var account = new Account { Username = "vn-learner", NormalizedUsername = "VN-LEARNER", MaxUses = 8, Used = 2 };
        db.Add(account);
        db.AddRange(new Account { Username = "exhausted-learner", NormalizedUsername = "EXHAUSTED-LEARNER", MaxUses = 9, Used = 9 },
            new Account { Username = "available-learner", NormalizedUsername = "AVAILABLE-LEARNER", MaxUses = 10, Used = 1 });
        var now = new Clock().GetUtcNow();
        for (var i = 0; i < 12; i++)
        {
            var session = new Session { AccountId = account.Id, AccountCredentialVersion = account.CredentialVersion,
                MachineId = new string('b', 64), LoginRequestId = Guid.NewGuid(), Mode = "Training", StartedAt = now.AddMinutes(-i), LastHeartbeatAt = now,
                EndedAt = i == 0 ? null : now, EndReason = i == 0 ? null : "UserEnded" };
            db.Add(session);
            db.Add(new Result { SessionId = session.Id, Score = i == 0 ? 700 : 699, Passed = i == 0,
                ReceivedAt = i == 0 ? DateTimeOffset.Parse("2026-11-02T17:00:00Z") : DateTimeOffset.Parse("2026-11-02T16:59:59Z").AddMinutes(-i),
                FlagsJson = i == 0 ? "[\"TimingInconsistent\"]" : "[]" });
        }
        for (var i = 0; i < 51; i++) db.Add(new AuditEntry { AccountId = account.Id, Action = "LearnerUpdated", CreatedAt = now.AddMinutes(-i) });
        await db.SaveChangesAsync();
        return (connection, account.Id);
    }
    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.GetStringAsync("/Admin/Login");
        using var response = await client.PostAsync("/Admin/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Username"] = "owner", ["Password"] = Password, ["__RequestVerificationToken"] = Token(login) }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task DashboardUsesVietnamDayAndExistingSessionPolicyAndServesLocalStylesWithCsp()
    {
        var (connection, _) = await SeedAsync();
        await using var factory = new ServerFactory(connection, clock: new Clock());
        using var client = factory.CreateHttpsClient();
        foreach (var path in new[] { "/Admin/Sessions", "/Admin/Audit" })
        { using var denied = await client.GetAsync(path); Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode); }
        await LoginAsync(client);
        using var response = await client.GetAsync("/Admin");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("<html lang=\"vi\">", html);
        Assert.Contains("Học viên còn lượt", html);
        Assert.Contains("<div class=\"value\">3</div>", html); // total learners
        Assert.Contains("<div class=\"value\">2</div>", html); // two learners have uses, fifteen uses remain
        Assert.DoesNotContain("<div class=\"value\">15</div>", html);
        Assert.Equal(2, Regex.Matches(html, "<div class=\"value\">1</div>").Count); // active session, today's result
        Assert.Equal(10, Regex.Matches(html, "/Admin/Results/Details/").Count);
        Assert.Contains("03/11/2026 00:00", html);
        foreach (var label in new[] { "Tổng quan", "Học viên", "Kết quả thi", "Phiên đăng nhập", "Nhật ký", "Đăng xuất" }) Assert.Contains(label, html);
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("https://", html);
        using var css = await client.GetAsync("/vendor/bootstrap/bootstrap.min.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Contains("Bootstrap  v5.3.8", await css.Content.ReadAsStringAsync());
        foreach (var page in new[] { ("dashboard", "/Admin"), ("learners", "/Admin/Accounts"),
            ("results", "/Admin/Results"), ("sessions", "/Admin/Sessions"), ("audit", "/Admin/Audit"), ("create", "/Admin/Accounts/Create") })
            await AdminHtmlCapture.SaveAsync(client, page.Item1, page.Item2);
    }

    [Fact]
    public async Task ResultFiltersConvertVietnamMidnightToUtcAndKeepVerdictFlagsAndAccount()
    {
        var (connection, _) = await SeedAsync();
        await using var factory = new ServerFactory(connection, clock: new Clock());
        using var client = factory.CreateHttpsClient(); await LoginAsync(client);
        var filtered = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Results?fromDate=2026-11-03&toDate=2026-11-03&account=VN-LEARNER&verdict=passed&flag=TimingInconsistent"));
        Assert.Single(Regex.Matches(filtered, "/Admin/Results/Details/").Cast<Match>());
        Assert.Contains("03/11/2026 00:00", filtered);
        Assert.Contains("700 / 1000", filtered); Assert.Contains("text-bg-success", filtered);
        Assert.Contains("title=\"Thời gian không nhất quán\"", filtered);
        var none = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Results?verdict=practice&flag=any"));
        Assert.Contains("Không có kết quả phù hợp.", none);
        using var invalid = await client.GetAsync("/Admin/Results?verdict=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("Bộ lọc không hợp lệ", await invalid.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GlobalSessionAndAuditFiltersPageAndHideFullMachineHash()
    {
        var (connection, _) = await SeedAsync();
        await using var factory = new ServerFactory(connection, clock: new Clock());
        using var client = factory.CreateHttpsClient(); await LoginAsync(client);
        var sessions = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Sessions?search=VN-LEARNER"));
        Assert.Contains("Luyện tập", sessions); Assert.Contains("03/11/2026 07:30", sessions);
        Assert.Contains("Người dùng thoát", sessions); Assert.Contains(new string('b', 12), sessions);
        Assert.DoesNotContain(new string('b', 64), sessions);
        var audit = WebUtility.HtmlDecode(await client.GetStringAsync("/Admin/Audit?account=vn-learner&actionFilter=LearnerUpdated"));
        Assert.Equal(50, Regex.Matches(audit, "title=\"LearnerUpdated\"").Count);
        Assert.Contains("Cập nhật học viên", audit); Assert.Contains("pageNumber=2", audit);
        var second = await client.GetStringAsync("/Admin/Audit?account=vn-learner&actionFilter=LearnerUpdated&pageNumber=2");
        Assert.Single(Regex.Matches(second, "title=\"LearnerUpdated\"").Cast<Match>());
        Assert.DoesNotContain("DetailsJson", audit);
    }

    [Fact]
    public async Task CreateAndEditVietnamExpiryPersistUtcAndEveryPostRequiresCsrf()
    {
        var (connection, id) = await SeedAsync();
        await using var factory = new ServerFactory(connection, clock: new Clock());
        using var client = factory.CreateHttpsClient(); await LoginAsync(client);
        foreach (var path in new[] { "/Admin/Accounts/Create", $"/Admin/Accounts/Edit/{id}", "/Admin/Logout" })
        { using var denied = await client.PostAsync(path, new FormUrlEncodedContent([])); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); }
        var create = await client.GetStringAsync("/Admin/Accounts/Create");
        using var created = await client.PostAsync("/Admin/Accounts/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Username"] = "new-learner", ["Password"] = "learner-password-123", ["MaxUses"] = "5", ["ExpiresUtc"] = "2026-11-04T00:15", ["__RequestVerificationToken"] = Token(create) }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        await using var db = PostgresFixture.Open(connection);
        var account = await db.Accounts.SingleAsync(a => a.Username == "new-learner");
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T17:15:00Z"), account.ExpiresAt);
        var edit = await client.GetStringAsync($"/Admin/Accounts/Edit/{account.Id}");
        Assert.Contains("2026-11-04T00:15", edit);
        using var saved = await client.PostAsync($"/Admin/Accounts/Edit/{account.Id}", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["AddUses"] = "2", ["ExpiresUtc"] = "2026-11-05T01:00", ["__RequestVerificationToken"] = Token(edit) }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await db.Entry(account).ReloadAsync(); Assert.Equal(7, account.MaxUses);
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T18:00:00Z"), account.ExpiresAt);
        using var bad = await client.PostAsync("/Admin/Accounts/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Username"] = "bad name", ["Password"] = "short", ["MaxUses"] = "abc", ["ExpiresUtc"] = "bad", ["__RequestVerificationToken"] = Token(create) }));
        var validation = WebUtility.HtmlDecode(await bad.Content.ReadAsStringAsync());
        Assert.Contains("Giá trị không hợp lệ", validation); Assert.Contains("Mật khẩu phải có", validation);
        Assert.DoesNotContain("The value", validation); Assert.DoesNotContain("short", validation);
    }

    [Theory]
    [InlineData(true, false, false, false, "Khóa")]
    [InlineData(false, true, false, false, "Khóa tạm")]
    [InlineData(false, false, true, false, "Hết hạn")]
    [InlineData(false, false, false, true, "Hết lượt")]
    [InlineData(false, false, false, false, "Hoạt động")]
    public void LearnerStatusIsVietnamese(bool locked, bool temporary, bool expired, bool exhausted, string expected)
    {
        var now = new Clock().GetUtcNow();
        Assert.Equal(expected, AdminDisplay.Status(new Account { Locked = locked, LockoutUntil = temporary ? now.AddMinutes(1) : null,
            ExpiresAt = expired ? now : null, MaxUses = exhausted ? 0 : 1 }, now));
    }
}
