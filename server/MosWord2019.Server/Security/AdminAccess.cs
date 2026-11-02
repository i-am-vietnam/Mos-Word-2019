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

    public static bool IsAllowed(Account account, DateTimeOffset now) =>
        account.IsAdmin && !account.Locked && (account.ExpiresAt == null || account.ExpiresAt > now);

    public async Task<Account?> AuthenticateAsync(ServerDbContext db, string username, string password,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        string normalized;
        try { normalized = NormalizeUsername(username); }
        catch (ArgumentException) { normalized = ""; }
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.NormalizedUsername == normalized, cancellationToken);
        var outcome = hasher.VerifyHashedPassword(account ?? new Account(), account?.PasswordHash ?? dummyHash, password);
        if (account == null || outcome == PasswordVerificationResult.Failed || !IsAllowed(account, now)) return null;
        if (outcome == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = hasher.HashPassword(account, password);
            await db.SaveChangesAsync(cancellationToken);
        }
        return account;
    }
}
