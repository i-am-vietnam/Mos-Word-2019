using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Packages;
using MosWord2019.Server.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Server.ResultHandling;

public sealed class ResultUploadEndpoint;

public sealed class ResultUploads(ServerDbContext db, LearnerSessions sessions, SessionTokens tokens,
    PackageStorage storage, TimeProvider time, ILogger<ResultUploads> logger)
{
    public const int MaximumPayloadBytes = 256 * 1024;

    public async Task<SessionOperation> UploadAsync(string? token, string? signature, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumPayloadBytes) return Error(400, "invalid_result");
        JObject payload;
        DateTimeOffset finalized;
        try { payload = ResultValidation.Parse(bytes); finalized = ResultValidation.FinalizedAt(payload); }
        catch (Exception ex) when (InvalidInput(ex)) { return Error(400, "invalid_result"); }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var accessIdentity = tokens.Read(token);
        var uploadIdentity = tokens.ReadUpload(token);
        (Session Session, Account Account)? found;
        bool active;
        if (accessIdentity != null)
        {
            found = await sessions.FindAsync(token, false, ct);
            active = found != null;
        }
        else
        {
            found = await FindUploadAsync(uploadIdentity, ct);
            active = found != null && SessionPolicy.Evaluate(found.Value.Account, found.Value.Session,
                time.GetUtcNow(), uploadIdentity!.CredentialVersion).Allowed;
        }
        if (found == null) { await transaction.CommitAsync(ct); return LearnerSessions.Invalid(); }
        var (session, account) = found.Value;
        var now = time.GetUtcNow();
        if (session.Mode != "Testing" || !Guid.TryParse((string?)payload["sessionId"], out var id) || id != session.Id ||
            !Verify(session.ResultSigningKey, signature, bytes))
        { await transaction.CommitAsync(ct); return LearnerSessions.Invalid(); }
        if (!active && (uploadIdentity == null || !SessionPolicy.CanUpload(account, session, now,
            uploadIdentity.CredentialVersion, finalized)))
        { await transaction.CommitAsync(ct); return LearnerSessions.Invalid(); }
        string hash = PackageStorage.Hash(bytes);
        var existing = await db.Results.SingleOrDefaultAsync(r => r.SessionId == session.Id, ct);
        if (existing != null)
        {
            if (existing.PayloadHash != hash)
            {
                Audit(session, "ResultUploadConflict", new { sessionId = session.Id, payloadHash = hash }, now);
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
                return Error(409, "result_conflict");
            }
            await transaction.CommitAsync(ct);
            return Receipt(existing, session, account, now, active && accessIdentity != null);
        }
        ValidatedResult validated;
        try { validated = ResultValidation.Validate(session, payload, now); }
        catch (Exception ex) when (InvalidInput(ex)) { await transaction.CommitAsync(ct); return Error(400, "invalid_result"); }
        string wrongTasks;
        string packageCode = "unresolved";
        try { wrongTasks = await ResultValidation.WrongTasksAsync(validated, db, storage, ct, code => packageCode = code); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or
            InvalidOperationException or JsonException or System.Text.DecoderFallbackException)
        {
            // Log only safe context, never the exception object/message or package contents.
            logger.LogWarning("Pinned instructions unavailable: sessionId={SessionId} packageCode={PackageCode} language={Language} exceptionType={ExceptionType}",
                session.Id, packageCode, validated.Language, ex.GetType().Name);
            await transaction.CommitAsync(ct);
            return Error(503, "pinned_instructions_unavailable");
        }
        var gaps = await db.OfflineGaps.Where(g => g.SessionId == session.Id).OrderBy(g => g.StartedAt).ToListAsync(ct);
        var lastContact = await db.Heartbeats.Where(h => h.SessionId == session.Id)
            .OrderByDescending(h => h.ReceivedAt).FirstOrDefaultAsync(ct);
        // An explicitly observed network outage may remain open when the client finalizes offline.
        // Silence alone cannot distinguish network loss from a crash and is never deducted.
        if (lastContact is { WasOffline: true } && validated.FinalizedAt >= lastContact.ReceivedAt)
        {
            var ongoing = gaps.LastOrDefault(g => g.Kind == "Network" && g.EndedAt == lastContact.ReceivedAt);
            if (ongoing != null) ongoing.EndedAt = validated.FinalizedAt;
        }
        var flags = new List<string>();
        bool inconsistent = session.TimingInconsistent || validated.FinalizedAt < session.ModeSelectedAt ||
            SessionTiming.IsInconsistent(session, validated.FinalizedAt, validated.ElapsedMilliseconds, gaps);
        if (inconsistent) { flags.Add("TimingInconsistent"); session.TimingInconsistent = true; }
        if (validated.ClockRollback)
        {
            flags.Add("ClockRollbackDetected");
            Audit(session, "ResultClockRollback", new { sessionId = session.Id }, now);
        }
        var result = new Result { SessionId = session.Id, PayloadHash = hash, PayloadJson = payload.ToString(Formatting.None),
            Score = validated.Score.Total, Passed = validated.Score.Passed, ReceivedAt = now,
            FlagsJson = JsonConvert.SerializeObject(flags), WrongTasksJson = wrongTasks,
            OfflineGapsJson = JsonConvert.SerializeObject(gaps.Select(g => new { startedAt = g.StartedAt, endedAt = g.EndedAt, kind = g.Kind })) };
        db.Results.Add(result);
        Audit(session, "ResultAccepted", new { sessionId = session.Id, payloadHash = hash, score = result.Score, flags }, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Receipt(result, session, account, now, active && accessIdentity != null);
    }

    // This possession token grants only result upload; it never grants heartbeat/package/mode access.
    private async Task<(Session Session, Account Account)?> FindUploadAsync(SessionIdentity? identity, CancellationToken ct)
    {
        if (identity == null) return null;
        var hint = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == identity.SessionId, ct);
        if (hint == null) return null;
        var account = (await db.Accounts.FromSqlInterpolated(
            $"SELECT * FROM \"Accounts\" WHERE \"Id\" = {hint.AccountId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (account == null) return null;
        await db.Entry(account).ReloadAsync(ct);
        var session = (await db.Sessions.FromSqlInterpolated(
            $"SELECT * FROM \"Sessions\" WHERE \"Id\" = {hint.Id} FOR UPDATE").ToListAsync(ct)).Single();
        await db.Entry(session).ReloadAsync(ct);
        var now = time.GetUtcNow();
        var permission = SessionPolicy.Evaluate(account, session, now, identity.CredentialVersion);
        if (!permission.Allowed && permission.EndReason != null)
        {
            var audit = SessionPolicy.End(session, now, permission.EndReason);
            if (audit != null)
            {
                db.AuditLog.Add(audit);
                var gap = SessionTiming.Gap(session, now, false);
                if (gap != null) db.OfflineGaps.Add(gap);
            }
            await db.SaveChangesAsync(ct);
        }
        return SessionPolicy.ValidIdentity(account, session, identity.CredentialVersion) ? (session, account) : null;
    }
    private SessionOperation Receipt(Result result, Session session, Account account, DateTimeOffset now, bool active) =>
        new(200, new { sessionId = result.SessionId, payloadHash = result.PayloadHash, score = result.Score, passed = result.Passed,
            flags = JsonConvert.DeserializeObject<string[]>(result.FlagsJson), serverTime = now,
            sessionToken = active ? tokens.Issue(session.Id, account.CredentialVersion) : null });
    private void Audit(Session session, string action, object details, DateTimeOffset now) =>
        db.AuditLog.Add(new AuditEntry { AccountId = session.AccountId, Action = action, CreatedAt = now, DetailsJson = JsonConvert.SerializeObject(details) });
    private static SessionOperation Error(int status, string error) => new(status, new SessionError(error, error.Replace('_', ' ')));
    private static bool InvalidInput(Exception ex) => ex is JsonException or InvalidDataException or FormatException or
        ArgumentException or InvalidCastException or OverflowException or NullReferenceException;
    private static bool Verify(string key, string? signature, byte[] bytes)
    {
        if (signature == null || signature.Length != 44) return false;
        byte[]? material = null;
        try
        {
            material = Convert.FromBase64String(key);
            var supplied = Convert.FromBase64String(signature);
            return material.Length == 32 && supplied.Length == 32 &&
                CryptographicOperations.FixedTimeEquals(supplied, HMACSHA256.HashData(material, bytes));
        }
        catch (FormatException) { return false; }
        finally { if (material != null) CryptographicOperations.ZeroMemory(material); }
    }

    public static void Map(WebApplication app) => app.MapPost("/api/results", async (HttpContext context, ResultUploads uploads, CancellationToken ct) =>
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MaximumPayloadBytes;
        if (context.Request.ContentLength > MaximumPayloadBytes) return Microsoft.AspNetCore.Http.Results.StatusCode(413);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int read = await context.Request.Body.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (body.Length + read > MaximumPayloadBytes) return Microsoft.AspNetCore.Http.Results.StatusCode(413);
            body.Write(buffer, 0, read);
        }
        var response = await uploads.UploadAsync(SessionTokens.Bearer(context), context.Request.Headers["X-Result-Signature"].ToString(), body.ToArray(), ct);
        return Microsoft.AspNetCore.Http.Results.Json(response.Body, statusCode: response.Status);
    }).WithMetadata(new ResultUploadEndpoint());
}
