using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Security;

public static class AdminBootstrap
{
    public static async Task CreateFirstAsync(ServerDbContext db, IPasswordHasher<Account> hasher,
        string username, string password, CancellationToken cancellationToken = default)
    {
        var normalized = AdminAccess.NormalizeUsername(username);
        if (password.Length < 12 || password.Length > 128 || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Admin password must contain 12-128 characters.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize bootstrap across processes; never reset or promote an existing account.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7142019)", cancellationToken);
        if (await db.Accounts.AnyAsync(x => x.IsAdmin, cancellationToken))
            throw new InvalidOperationException("An administrator already exists. Bootstrap cannot overwrite accounts.");
        if (await db.Accounts.AnyAsync(x => x.NormalizedUsername == normalized, cancellationToken))
            throw new InvalidOperationException("Username is already in use.");
        var account = new Account { Username = username, NormalizedUsername = normalized, IsAdmin = true };
        account.PasswordHash = hasher.HashPassword(account, password);
        db.Accounts.Add(account);
        db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "AdminBootstrapped" });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
