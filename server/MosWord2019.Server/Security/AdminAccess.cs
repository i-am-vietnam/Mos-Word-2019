using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Security;

public sealed partial class AdminAccess(IPasswordHasher<Account> hasher)
{
    private readonly string dummyHash = hasher.HashPassword(new Account(), Guid.NewGuid().ToString("N"));

    [GeneratedRegex("\\A[A-Za-z0-9._-]{3,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public static string NormalizeUsername(string username)
    {
        if (!UsernamePattern().IsMatch(username))
            throw new ArgumentException("Username must contain 3-64 ASCII letters, digits, dots, underscores or hyphens.");
        return username.ToUpperInvariant();
    }

    public static bool IsAllowed(Account account, DateTimeOffset now, bool newLogin = true) =>
        account.IsAdmin && !account.Locked && (!newLogin || account.LockoutUntil == null || account.LockoutUntil <= now) &&
        (account.ExpiresAt == null || account.ExpiresAt > now);

    public async Task<Account?> AuthenticateAsync(ServerDbContext db, string username, string password,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var account = await VerifyAsync(db, username, password, true, now, cancellationToken);
        var allowed = account != null && IsAllowed(account, now);
        if (allowed) ResetFailures(account!);
        else if (account != null) db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "LoginFailed", DetailsJson = "{\"channel\":\"admin\",\"reason\":\"account_unavailable\"}" });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return allowed ? account : null;
    }

    public static void ResetFailures(Account account)
    {
        account.FailedLoginCount = 0;
        account.LockoutUntil = null;
    }

    // Caller holds a transaction so failed attempts and successful uses serialize on the same account.
    public async Task<Account?> VerifyAsync(ServerDbContext db, string username, string password,
        bool admin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string normalized;
        try { normalized = NormalizeUsername(username); }
        catch (ArgumentException) { normalized = ""; }
        var accounts = await db.Accounts.FromSqlInterpolated(
            $"SELECT * FROM \"Accounts\" WHERE \"NormalizedUsername\" = {normalized} FOR UPDATE").ToListAsync(cancellationToken);
        var account = accounts.SingleOrDefault();
        if (account != null) await db.Entry(account).ReloadAsync(cancellationToken);
        var outcome = hasher.VerifyHashedPassword(account ?? new Account(), account?.PasswordHash ?? dummyHash, password);
        if (account == null || outcome == PasswordVerificationResult.Failed || account.IsAdmin != admin || account.LockoutUntil > now)
        {
            db.AuditLog.Add(new AuditEntry { AccountId = account?.Id, Action = "LoginFailed",
                DetailsJson = admin ? "{\"channel\":\"admin\"}" : "{\"channel\":\"learner\"}" });
            if (account != null && !(account.LockoutUntil > now))
            {
                if (account.LockoutUntil != null) account.FailedLoginCount = 0;
                account.LockoutUntil = null;
                account.FailedLoginCount = Math.Min(10, account.FailedLoginCount + 1);
                if (account.FailedLoginCount == 10)
                {
                    account.LockoutUntil = now.AddMinutes(15);
                    db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "AccountTemporarilyLocked" });
                }
            }
            return null;
        }
        if (outcome == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = hasher.HashPassword(account, password);
        }
        return account;
    }
}
