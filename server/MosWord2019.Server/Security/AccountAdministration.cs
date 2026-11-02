using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using Npgsql;

namespace MosWord2019.Server.Security;

public sealed record AccountAdministrationResult(bool Success, string? Error = null, Guid? AccountId = null);

public sealed class AccountAdministration(ServerDbContext db, IPasswordHasher<Account> hasher)
{
    public static bool ValidPassword(string? password) =>
        password is { Length: >= 12 and <= 128 } && !string.IsNullOrWhiteSpace(password);

    public async Task<AccountAdministrationResult> CreateAsync(string username, string password, int maxUses,
        DateTimeOffset? expiresAt, bool locked, CancellationToken cancellationToken = default)
    {
        string normalized;
        try { normalized = AdminAccess.NormalizeUsername(username); }
        catch (ArgumentException exception) { return new(false, exception.Message); }
        if (!ValidPassword(password)) return new(false, "Password must contain 12-128 characters and cannot be blank.");
        if (maxUses < 0) return new(false, "Maximum uses cannot be negative.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var account = new Account
        {
            Username = username, NormalizedUsername = normalized, IsAdmin = false,
            MaxUses = maxUses, ExpiresAt = expiresAt?.ToUniversalTime(), Locked = locked
        };
        account.PasswordHash = hasher.HashPassword(account, password);
        db.Accounts.Add(account);
        Audit(account, "LearnerCreated", new { account.Username, account.MaxUses, account.ExpiresAt, account.Locked });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true, AccountId: account.Id);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return new(false, "Username is already in use.");
        }
    }

    public async Task<AccountAdministrationResult> UpdateAsync(Guid id, int? maxUses, int addUses,
        DateTimeOffset? expiresAt, bool locked, string? newPassword, CancellationToken cancellationToken = default)
    {
        if (addUses < 0) return new(false, "Additional uses cannot be negative.");
        if (newPassword != null && !ValidPassword(newPassword))
            return new(false, "Password must contain 12-128 characters and cannot be blank.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var account = await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (account == null || account.IsAdmin) return new(false, "Learner account was not found.");
        // Refresh a previously tracked entity after acquiring the database row lock.
        await db.Entry(account).ReloadAsync(cancellationToken);
        if (account.IsAdmin) return new(false, "Learner account was not found.");
        var proposed = (long)(maxUses ?? account.MaxUses) + addUses;
        if (maxUses < account.Used || proposed < account.Used || proposed > int.MaxValue)
            return new(false, "Maximum uses must be at least the used count and cannot exceed 2147483647.");
        var before = new { account.MaxUses, account.ExpiresAt, account.Locked };
        account.MaxUses = (int)proposed;
        account.ExpiresAt = expiresAt?.ToUniversalTime();
        account.Locked = locked;
        if (newPassword != null)
        {
            account.PasswordHash = hasher.HashPassword(account, newPassword);
            account.FailedLoginCount = 0;
            account.LockoutUntil = null;
            account.CredentialVersion = Guid.NewGuid();
        }
        Audit(account, "LearnerUpdated", new
        {
            Before = before, After = new { account.MaxUses, account.ExpiresAt, account.Locked },
            AddedUses = addUses, PasswordReset = newPassword != null
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, AccountId: account.Id);
    }

    private void Audit(Account account, string action, object details) => db.AuditLog.Add(new AuditEntry
    { AccountId = account.Id, Action = action, DetailsJson = JsonSerializer.Serialize(details) });
}
