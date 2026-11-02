using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MosWord2019.Server.Data;
using Npgsql;

namespace MosWord2019.Server.Security;

public sealed record LoginRequest(string? Username, string? Password, string? MachineId, Guid IdempotencyKey);
public sealed record ResumeRequest(string? ResumeToken, string? MachineId);
public sealed record ModeRequest(string? Mode);
public sealed record SessionReply(Guid SessionId, string SessionToken, string ResumeToken, int RemainingUses,
    DateTimeOffset ServerTime, string? Mode);
public sealed record SessionError(string Error, string Message);
public sealed record SessionOperation(int Status, object Body);

public sealed partial class LearnerSessions(ServerDbContext db, AdminAccess credentials, SessionTokens tokens, TimeProvider time)
{
    [GeneratedRegex("\\A[0-9a-f]{64}\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachinePattern();
    public static bool ValidMachine(string? machine) => machine != null && MachinePattern().IsMatch(machine);
    public static SessionOperation Invalid() => new(401, new SessionError("invalid_credentials", "Invalid credentials. Please try again later."));
    private static SessionOperation Error(int status, string code) => new(status, new SessionError(code, code.Replace('_', ' ')));
    private void AuditDenied(Account account, string reason) => db.AuditLog.Add(new AuditEntry
    { AccountId = account.Id, Action = "LoginFailed", DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { channel = "learner", reason }) });
    private SessionOperation Reply(Session session, Account account, DateTimeOffset now) => new(200,
        new SessionReply(session.Id, tokens.Issue(session.Id, account.CredentialVersion),
            tokens.Issue(session.Id, account.CredentialVersion, true), account.MaxUses - account.Used, now, session.Mode));

    public async Task<SessionOperation> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        if (!ValidMachine(request.MachineId) || request.IdempotencyKey == Guid.Empty ||
            string.IsNullOrEmpty(request.Username) || request.Username.Length > 64 ||
            string.IsNullOrEmpty(request.Password) || request.Password.Length > 128) return Error(400, "invalid_request");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var account = await credentials.VerifyAsync(db, request.Username, request.Password, false, now, ct);
        await db.SaveChangesAsync(ct);
        if (account == null) { await transaction.CommitAsync(ct); return Invalid(); }
        if (account.Locked || account.ExpiresAt <= now)
        {
            AuditDenied(account, "account_unavailable");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Error(403, account.Locked ? "account_locked" : "account_expired");
        }
        var previous = await db.Sessions.SingleOrDefaultAsync(x => x.AccountId == account.Id &&
            x.LoginRequestId == request.IdempotencyKey, ct);
        if (previous != null)
        {
            if (previous.MachineId != request.MachineId!.ToLowerInvariant() || previous.EndedAt != null ||
                previous.AccountCredentialVersion != account.CredentialVersion) return Error(409, "idempotency_conflict");
            AdminAccess.ResetFailures(account);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Reply(previous, account, now);
        }
        // The conditional UPDATE is the authority for consuming a use, even under concurrent login.
        await using var update = new NpgsqlCommand("""
            UPDATE "Accounts" SET "Used" = "Used" + 1
            WHERE "Id" = @id AND "Used" < "MaxUses" AND NOT "Locked" AND NOT "IsAdmin"
              AND ("ExpiresAt" IS NULL OR "ExpiresAt" > @now)
              AND ("LockoutUntil" IS NULL OR "LockoutUntil" <= @now)
            RETURNING "Used"
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
        update.Parameters.AddWithValue("id", account.Id);
        update.Parameters.AddWithValue("now", now);
        var used = await update.ExecuteScalarAsync(ct);
        if (used == null)
        {
            AuditDenied(account, "no_uses_left");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Error(403, "no_uses_left");
        }
        // Reload instead of writing the tracked counter back over the atomic SQL update.
        await db.Entry(account).ReloadAsync(ct);
        AdminAccess.ResetFailures(account);
        var session = new Session { AccountId = account.Id, AccountCredentialVersion = account.CredentialVersion,
            LoginRequestId = request.IdempotencyKey, MachineId = request.MachineId!.ToLowerInvariant(),
            StartedAt = now, LastHeartbeatAt = now };
        db.Sessions.Add(session);
        db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "LearnerLogin", CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Reply(session, account, now);
    }

    private async Task<(Session Session, Account Account)?> FindAsync(string? token, bool resume, CancellationToken ct)
    {
        var identity = tokens.Read(token, resume);
        if (identity == null) return null;
        var hint = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == identity.SessionId, ct);
        if (hint == null) return null;
        var accounts = await db.Accounts.FromSqlInterpolated(
            $"SELECT * FROM \"Accounts\" WHERE \"Id\" = {hint.AccountId} FOR UPDATE").ToListAsync(ct);
        var account = accounts.SingleOrDefault();
        if (account == null) return null;
        await db.Entry(account).ReloadAsync(ct);
        var sessions = await db.Sessions.FromSqlInterpolated(
            $"SELECT * FROM \"Sessions\" WHERE \"Id\" = {hint.Id} FOR UPDATE").ToListAsync(ct);
        var session = sessions.Single();
        await db.Entry(session).ReloadAsync(ct);
        var now = time.GetUtcNow();
        if (account.IsAdmin || account.Locked || account.ExpiresAt <= now || account.LockoutUntil > now ||
            identity.CredentialVersion != account.CredentialVersion || session.AccountCredentialVersion != account.CredentialVersion)
            return null;
        return (session, account);
    }

    public async Task<SessionOperation> ModeAsync(string? token, ModeRequest request, CancellationToken ct)
    {
        if (request.Mode is not ("Training" or "Testing")) return Error(400, "invalid_mode");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await FindAsync(token, false, ct);
        if (found == null || found.Value.Session.EndedAt != null) return Invalid();
        var (session, account) = found.Value;
        if (session.Mode != null && session.Mode != request.Mode) return Error(409, "mode_already_selected");
        var now = time.GetUtcNow();
        if (request.Mode == "Training" && now >= session.StartedAt.AddHours(4)) return Error(409, "session_expired");
        if (session.Mode == null)
        {
            session.Mode = request.Mode;
            session.LastHeartbeatAt = now;
            db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "SessionModeSelected", CreatedAt = now });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Reply(session, account, now);
    }

    public async Task<SessionOperation> EndAsync(string? token, Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await FindAsync(token, false, ct);
        if (found == null || found.Value.Session.Id != id) return Invalid();
        var (session, account) = found.Value;
        var now = time.GetUtcNow();
        if (session.EndedAt == null)
        {
            session.EndedAt = now;
            db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "SessionEnded", CreatedAt = now });
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(200, new { sessionId = session.Id, serverTime = now, endedAt = session.EndedAt });
    }

    public async Task<SessionOperation> ResumeAsync(ResumeRequest request, CancellationToken ct)
    {
        if (!ValidMachine(request.MachineId)) return Error(400, "invalid_request");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await FindAsync(request.ResumeToken, true, ct);
        if (found == null) return Invalid();
        var (session, account) = found.Value;
        var now = time.GetUtcNow();
        var lastSeen = session.LastHeartbeatAt ?? session.StartedAt;
        if (session.EndedAt != null || session.MachineId != request.MachineId!.ToLowerInvariant() ||
            now < lastSeen || now - lastSeen > TimeSpan.FromMinutes(15) ||
            (session.Mode == "Training" && now >= session.StartedAt.AddHours(4))) return Invalid();
        session.LastHeartbeatAt = now;
        db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "SessionResumed", CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Reply(session, account, now);
    }
}
