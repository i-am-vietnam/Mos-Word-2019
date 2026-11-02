using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AccountAdministrationTests(PostgresFixture postgres)
{
    private const string Password = "initial-password-123";
    private static AccountAdministration Service(ServerDbContext db) => new(db, new PasswordHasher<Account>());

    [Fact]
    public async Task CreateLearnerPersistsNormalizedHashUtcAndSafeAudit()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var expiry = DateTimeOffset.UtcNow.AddDays(2).ToOffset(TimeSpan.FromHours(7));
        Guid id;
        await using (var db = PostgresFixture.Open(connection))
        {
            var result = await Service(db).CreateAsync("Learner_01", Password, 7, expiry, true);
            Assert.True(result.Success);
            id = result.AccountId!.Value;
        }
        await using var persisted = PostgresFixture.Open(connection);
        var account = await persisted.Accounts.SingleAsync(x => x.Id == id);
        Assert.Equal("LEARNER_01", account.NormalizedUsername);
        Assert.False(account.IsAdmin);
        Assert.True(account.Locked);
        Assert.Equal(7, account.MaxUses);
        Assert.Equal(0, account.Used);
        Assert.Equal(expiry.ToUnixTimeMilliseconds(), account.ExpiresAt!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(TimeSpan.Zero, account.ExpiresAt.Value.Offset);
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<Account>().VerifyHashedPassword(account, account.PasswordHash, Password));
        var audit = await persisted.AuditLog.SingleAsync();
        Assert.Equal(id, audit.AccountId);
        Assert.DoesNotContain(Password, audit.DetailsJson);
        Assert.DoesNotContain(account.PasswordHash, audit.DetailsJson);
    }

    [Theory]
    [InlineData("bad name", Password, 1)]
    [InlineData("valid", "short", 1)]
    [InlineData("valid", "            ", 1)]
    [InlineData("valid", Password, -1)]
    public async Task InvalidCreateDoesNotPersist(string username, string password, int maxUses)
    {
        await using var db = PostgresFixture.Open(await postgres.CreateDatabaseAsync());
        Assert.False((await Service(db).CreateAsync(username, password, maxUses, null, false)).Success);
        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.AuditLog.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicateUsernameIsControlledAndNeverPromotes()
    {
        var connection = await postgres.CreateDatabaseAsync();
        async Task<AccountAdministrationResult> Create(string username)
        {
            await using var db = PostgresFixture.Open(connection);
            return await Service(db).CreateAsync(username, Password, 1, null, false);
        }
        var results = await Task.WhenAll(Create("learner"), Create("LEARNER"));
        Assert.Single(results, x => x.Success);
        Assert.Contains("already in use", Assert.Single(results, x => !x.Success).Error!);
        await using var persisted = PostgresFixture.Open(connection);
        Assert.False((await persisted.Accounts.SingleAsync()).IsAdmin);
        Assert.Single(await persisted.AuditLog.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentAddUsesHasNoLostUpdatesAndOverflowDoesNotMutate()
    {
        var connection = await postgres.CreateDatabaseAsync();
        Guid id;
        await using (var db = PostgresFixture.Open(connection))
            id = (await Service(db).CreateAsync("learner", Password, 4, null, false)).AccountId!.Value;
        async Task<AccountAdministrationResult> Add(int count)
        {
            await using var db = PostgresFixture.Open(connection);
            return await Service(db).UpdateAsync(id, null, count, null, false, null);
        }
        var results = await Task.WhenAll(Add(3), Add(5), Add(7));
        Assert.All(results, result => Assert.True(result.Success));
        Assert.False((await Add(int.MaxValue)).Success);
        await using var persisted = PostgresFixture.Open(connection);
        Assert.Equal(19, (await persisted.Accounts.SingleAsync()).MaxUses);
        Assert.Equal(4, await persisted.AuditLog.CountAsync());
    }

    [Fact]
    public async Task EditEnforcesUsedResetsCredentialsAndClearsExpiry()
    {
        var connection = await postgres.CreateDatabaseAsync();
        Guid id;
        Guid version;
        await using (var db = PostgresFixture.Open(connection))
        {
            id = (await Service(db).CreateAsync("learner", Password, 5, DateTimeOffset.UtcNow.AddDays(1), false)).AccountId!.Value;
            var account = await db.Accounts.SingleAsync();
            account.Used = 3;
            account.FailedLoginCount = 4;
            account.LockoutUntil = DateTimeOffset.UtcNow.AddHours(1);
            version = account.CredentialVersion;
            await db.SaveChangesAsync();
        }
        await using (var db = PostgresFixture.Open(connection))
        {
            Assert.False((await Service(db).UpdateAsync(id, 2, 0, null, false, null)).Success);
            Assert.True((await Service(db).UpdateAsync(id, 3, 2, null, true, "replacement-password-456")).Success);
        }
        await using var persisted = PostgresFixture.Open(connection);
        var saved = await persisted.Accounts.SingleAsync();
        Assert.Equal(5, saved.MaxUses);
        Assert.Equal(3, saved.Used);
        Assert.True(saved.Locked);
        Assert.Null(saved.ExpiresAt);
        Assert.Equal(0, saved.FailedLoginCount);
        Assert.Null(saved.LockoutUntil);
        Assert.NotEqual(version, saved.CredentialVersion);
        var hasher = new PasswordHasher<Account>();
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(saved, saved.PasswordHash, Password));
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(saved, saved.PasswordHash, "replacement-password-456"));
        var audits = await persisted.AuditLog.Select(x => x.DetailsJson).ToListAsync();
        Assert.All(audits, audit => Assert.DoesNotContain("replacement-password-456", audit));
    }

    [Fact]
    public async Task AdministratorAndMissingAccountsCannotBeEdited()
    {
        await using var db = PostgresFixture.Open(await postgres.CreateDatabaseAsync());
        await AdminBootstrap.CreateFirstAsync(db, new PasswordHasher<Account>(), "owner", Password);
        var admin = await db.Accounts.SingleAsync();
        Assert.False((await Service(db).UpdateAsync(admin.Id, 10, 3, null, true, "replacement-password-456")).Success);
        Assert.False((await Service(db).UpdateAsync(Guid.NewGuid(), 10, 0, null, false, null)).Success);
        await db.Entry(admin).ReloadAsync();
        Assert.True(admin.IsAdmin);
        Assert.False(admin.Locked);
        Assert.Equal(0, admin.MaxUses);
        Assert.Single(await db.AuditLog.ToListAsync());
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, "short")]
    [InlineData(0, "            ")]
    public async Task InvalidUpdateDoesNotChangeAccount(int addUses, string? password)
    {
        await using var db = PostgresFixture.Open(await postgres.CreateDatabaseAsync());
        var id = (await Service(db).CreateAsync("learner", Password, 5, null, false)).AccountId!.Value;
        Assert.False((await Service(db).UpdateAsync(id, null, addUses, null, true, password)).Success);
        await db.ChangeTracker.Entries<Account>().Single().ReloadAsync();
        Assert.False((await db.Accounts.SingleAsync()).Locked);
        Assert.Single(await db.AuditLog.ToListAsync());
    }
}
