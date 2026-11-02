using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;
using Npgsql;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class DatabaseTests(PostgresFixture postgres)
{
    private static PasswordHasher<Account> Hasher() => new();
    private static string Password() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task C0DatabaseUpgradePreservesExistingAdminHashCountersAndExpiry()
    {
        var connection = await postgres.CreateDatabaseAsync(migrate: false);
        var id = Guid.NewGuid();
        var password = Password();
        var account = new Account { Id = id };
        var hash = Hasher().HashPassword(account, password);
        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds());
        await using var db = PostgresFixture.Open(connection);
        await db.GetService<IMigrator>().MigrateAsync("20261102023613_InitialFoundation");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Accounts" ("Id", "Username", "NormalizedUsername", "PasswordHash", "IsAdmin", "MaxUses", "Used", "Locked", "ExpiresAt")
            VALUES ({id}, 'owner', 'OWNER', {hash}, TRUE, 9, 3, FALSE, {expiry})
            """);
        await db.Database.MigrateAsync();
        var upgraded = await db.Accounts.SingleAsync();
        Assert.Equal(hash, upgraded.PasswordHash);
        Assert.Equal(3, upgraded.Used);
        Assert.Equal(9, upgraded.MaxUses);
        Assert.Equal(expiry, upgraded.ExpiresAt);
        Assert.NotEqual(Guid.Empty, upgraded.CredentialVersion);
        Assert.Equal(0, upgraded.FailedLoginCount);
        Assert.Null(upgraded.LockoutUntil);
    }

    [Fact]
    public async Task MigrationsCreateAllFiveTablesAndCanRollBackAndReapply()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.Open(connection);
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.Sessions.ToListAsync());
        Assert.Empty(await db.Results.ToListAsync());
        Assert.Empty(await db.Packages.ToListAsync());
        Assert.Empty(await db.AuditLog.ToListAsync());
        Assert.Empty(await db.Heartbeats.ToListAsync());
        Assert.Empty(await db.OfflineGaps.ToListAsync());
        await db.GetService<IMigrator>().MigrateAsync("0");
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Accounts.ToListAsync());
    }

    [Fact]
    public async Task BootstrapPersistsHashAuditAndUtcAndRefusesOverwrite()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var password = Password();
        await using (var db = PostgresFixture.Open(connection))
            await AdminBootstrap.CreateFirstAsync(db, Hasher(), "Owner", password);
        await using var reopened = PostgresFixture.Open(connection);
        var account = await reopened.Accounts.SingleAsync();
        Assert.True(account.IsAdmin);
        Assert.Equal("OWNER", account.NormalizedUsername);
        Assert.NotEqual(password, account.PasswordHash);
        Assert.NotEqual(PasswordVerificationResult.Failed,
            Hasher().VerifyHashedPassword(account, account.PasswordHash, password));
        Assert.Equal(0, account.Used);
        Assert.Equal(TimeSpan.Zero, account.CreatedAt.Offset);
        Assert.InRange(account.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var audit = await reopened.AuditLog.SingleAsync();
        Assert.Equal(account.Id, audit.AccountId);
        Assert.Equal("AdminBootstrapped", audit.Action);
        Assert.DoesNotContain(password, audit.DetailsJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AdminBootstrap.CreateFirstAsync(reopened, Hasher(), "other", Password()));
        Assert.Single(await reopened.Accounts.ToListAsync());
    }

    [Theory]
    [InlineData("ab", "too-short")]
    [InlineData("bad user", "a password long enough")]
    [InlineData("owner", "short")]
    [InlineData("owner", "            ")]
    public async Task InvalidBootstrapLeavesDatabaseUntouched(string username, string password)
    {
        await using var db = PostgresFixture.Open(await postgres.CreateDatabaseAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => AdminBootstrap.CreateFirstAsync(db, Hasher(), username, password));
        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.AuditLog.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentBootstrapCreatesExactlyOneAdministratorAndAuditEntry()
    {
        var connection = await postgres.CreateDatabaseAsync();
        async Task<bool> TryCreate(string username)
        {
            await using var db = PostgresFixture.Open(connection);
            try { await AdminBootstrap.CreateFirstAsync(db, Hasher(), username, Password()); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(TryCreate("owner1"), TryCreate("owner2"));
        Assert.Single(outcomes, x => x);
        await using var db = PostgresFixture.Open(connection);
        Assert.Single(await db.Accounts.ToListAsync());
        Assert.Single(await db.AuditLog.ToListAsync());
    }

    [Fact]
    public async Task PersistenceRetainsRelationshipsAndJsonAcrossContexts()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", PasswordHash = "test-only" };
        var session = new Session { AccountId = account.Id, LoginRequestId = Guid.NewGuid(), MachineId = new string('a', 64), Mode = "Training" };
        await using (var db = PostgresFixture.Open(connection))
        {
            db.AddRange(account, session, new Result { SessionId = session.Id, PayloadJson = "{\"score\":700}" },
                new Package { Code = "P01", Version = "1", Sha256 = new string('b', 64), StoragePath = "test/package.zip", SizeBytes = 123 });
            await db.SaveChangesAsync();
        }
        await using var reopened = PostgresFixture.Open(connection);
        Assert.Equal(account.Id, (await reopened.Sessions.SingleAsync()).AccountId);
        Assert.Contains("700", (await reopened.Results.SingleAsync()).PayloadJson);
        Assert.Equal(123, (await reopened.Packages.SingleAsync()).SizeBytes);
        Assert.Equal(TimeSpan.Zero, (await reopened.Sessions.SingleAsync()).StartedAt.Offset);
    }

    [Theory]
    [InlineData("UPDATE \"Accounts\" SET \"Used\" = -1", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE \"Accounts\" SET \"Used\" = 1", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE \"Sessions\" SET \"Mode\" = 'Other'", PostgresErrorCodes.CheckViolation)]
    [InlineData("UPDATE \"Sessions\" SET \"MachineId\" = 'personal data'", PostgresErrorCodes.CheckViolation)]
    [InlineData("DELETE FROM \"Accounts\"", PostgresErrorCodes.ForeignKeyViolation)]
    public async Task DatabaseRejectsInvalidCountersModesMachineIdsAndOrphanSessions(string sql, string code)
    {
        await using var db = PostgresFixture.Open(await postgres.CreateDatabaseAsync());
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", PasswordHash = "test-only" };
        db.AddRange(account, new Session { AccountId = account.Id, LoginRequestId = Guid.NewGuid(), MachineId = new string('a', 64) });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Equal(code, error.SqlState);
    }

    [Fact]
    public async Task DatabaseRejectsDuplicateUsernameAndLoginRequest()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER", PasswordHash = "test-only" };
        var request = Guid.NewGuid();
        await using (var db = PostgresFixture.Open(connection))
        {
            db.AddRange(account, new Session { AccountId = account.Id, LoginRequestId = request, MachineId = new string('a', 64) });
            await db.SaveChangesAsync();
        }
        await using (var db = PostgresFixture.Open(connection))
        {
            db.Accounts.Add(new Account { Username = "Learner", NormalizedUsername = "LEARNER", PasswordHash = "test-only" });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using (var db = PostgresFixture.Open(connection))
        {
            db.Sessions.Add(new Session { AccountId = account.Id, LoginRequestId = request, MachineId = new string('b', 64) });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }
}
