using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MosWord2019.Server.Data;
using MosWord2019.Server.Packages;
using Npgsql;

namespace MosWord2019.Server.Security;

public sealed record LoginRequest(string? Username, string? Password, string? MachineId, Guid IdempotencyKey);
public sealed record ResumeRequest(string? ResumeToken, string? MachineId);
public sealed record ModeRequest(string? Mode);
public sealed record SessionReply(Guid SessionId, string SessionToken, string ResumeToken, int RemainingUses,
    DateTimeOffset ServerTime, string? Mode, long? ExamSeed = null, string? DefinitionHash = null,
    string? UploadToken = null, string? ResultSigningKey = null, DateTimeOffset? ModeSelectedAt = null);
public sealed record SessionError(string Error, string Message);
public sealed record SessionOperation(int Status, object Body);

public sealed partial class LearnerSessions(ServerDbContext db, AdminAccess credentials, SessionTokens tokens,
    TimeProvider time, IServerExamPool? examPool = null)
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
            tokens.Issue(session.Id, account.CredentialVersion, true), account.MaxUses - account.Used, now, session.Mode,
            session.ExamSeed, session.ExamDefinitionHash,
            session.Mode == "Testing" ? tokens.IssueUpload(session.Id, account.CredentialVersion) : null,
            session.ResultSigningKey, session.ModeSelectedAt));

    public async Task<SessionOperation> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        if (!ValidMachine(request.MachineId) || request.IdempotencyKey == Guid.Empty ||
            string.IsNullOrEmpty(request.Username) || request.Username.Length > 64 ||
            string.IsNullOrEmpty(request.Password) || request.Password.Length > 128) return Error(400, "invalid_request");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var account = await credentials.VerifyAsync(db, request.Username, request.Password, false, now, ct);
        // Verification takes the account row lock; concurrent retries may have waited for a newer session.
        now = time.GetUtcNow();
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
            var permission = SessionPolicy.Evaluate(account, previous, now, account.CredentialVersion);
            if (!permission.Allowed && permission.EndReason != null)
            {
                var audit = SessionPolicy.End(previous, now, permission.EndReason);
                if (audit != null) { db.AuditLog.Add(audit); RecordGap(previous, now); }
            }
            if (previous.MachineId != request.MachineId!.ToLowerInvariant() || !permission.Allowed)
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Error(409, "idempotency_conflict");
            }
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

    // Caller owns a transaction and commits denied results to preserve expiration and audit.
    public Task<(Session Session, Account Account)?> FindAsync(string? token, bool resume, CancellationToken ct) =>
        FindCoreAsync(token, resume, false, ct);

    private async Task<(Session Session, Account Account)?> FindCoreAsync(string? token, bool resume,
        bool allowEndReceipt, CancellationToken ct)
    {
        var identity = tokens.Read(token, resume);
        var tokenActive = identity != null;
        identity ??= tokens.ReadExpirationHint(token, resume);
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
        if (tokenActive && allowEndReceipt && session.EndReason == "UserEnded" && session.EndedAt != null &&
            SessionPolicy.ValidIdentity(account, session, identity.CredentialVersion) &&
            (account.ExpiresAt == null || account.ExpiresAt > now)) return (session, account);
        var permission = SessionPolicy.Evaluate(account, session, now, identity.CredentialVersion);
        if (!permission.Allowed)
        {
            if (permission.EndReason != null)
            {
                var audit = SessionPolicy.End(session, now, permission.EndReason);
                if (audit != null) { db.AuditLog.Add(audit); RecordGap(session, now); }
                await db.SaveChangesAsync(ct);
            }
            return null;
        }
        // Resume proves possession of the session secret; password lockout only blocks new login.
        // Refusing a stale resume must not terminate a running client still inside its active window.
        return tokenActive && (!resume || SessionPolicy.CanResume(session, now)) ? (session, account) : null;
    }

    public async Task<SessionOperation> ModeAsync(string? token, ModeRequest request, CancellationToken ct)
    {
        if (request.Mode is not ("Training" or "Testing")) return Error(400, "invalid_mode");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await FindAsync(token, false, ct);
        if (found == null) { await transaction.CommitAsync(ct); return Invalid(); }
        var (session, account) = found.Value;
        if (session.Mode != null && session.Mode != request.Mode) return Error(409, "mode_already_selected");
        var now = time.GetUtcNow();
        if (session.Mode == null)
        {
            if (request.Mode == "Testing")
            {
                try
                {
                    var exam = await (examPool ?? new ServerExamPool(db)).CreateAsync(ct);
                    session.ExamSeed = exam.Seed;
                    session.ExamDefinitionHash = exam.DefinitionHash;
                    session.ExamSnapshotJson = exam.SnapshotJson;
                }
                catch (InvalidDataException) { return Error(503, "packages_unavailable"); }
            }
            session.Mode = request.Mode;
            // PostgreSQL timestamps retain microseconds; the first reply must match later reads exactly.
            session.ModeSelectedAt = new DateTimeOffset(now.Ticks - now.Ticks % 10, now.Offset);
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
        var found = await FindCoreAsync(token, false, true, ct);
        if (found == null || found.Value.Session.Id != id) { await transaction.CommitAsync(ct); return Invalid(); }
        var (session, account) = found.Value;
        var now = time.GetUtcNow();
        if (session.EndedAt == null)
        {
            var audit = SessionPolicy.End(session, now, "UserEnded");
            if (audit != null) { db.AuditLog.Add(audit); RecordGap(session, now); }
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(200, new { sessionId = session.Id, serverTime = now, endedAt = session.EndedAt,
            sessionToken = tokens.Issue(session.Id, account.CredentialVersion) });
    }

    public async Task<SessionOperation> ResumeAsync(ResumeRequest request, CancellationToken ct)
    {
        if (!ValidMachine(request.MachineId)) return Error(400, "invalid_request");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await FindAsync(request.ResumeToken, true, ct);
        if (found == null) { await transaction.CommitAsync(ct); return Invalid(); }
        var (session, account) = found.Value;
        var now = time.GetUtcNow();
        if (session.MachineId != request.MachineId!.ToLowerInvariant()) return Invalid();
        RecordGap(session, now);
        session.LastHeartbeatAt = now;
        db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "SessionResumed", CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Reply(session, account, now);
    }

    private void RecordGap(Session session, DateTimeOffset now)
    {
        var gap = SessionTiming.Gap(session, now, false);
        if (gap != null) db.OfflineGaps.Add(gap);
    }
}
