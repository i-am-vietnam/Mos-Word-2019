using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Security;

public sealed record HeartbeatRequest(long? ClientElapsedMilliseconds = null, bool WasOffline = false);
public sealed record HeartbeatReply(Guid SessionId, string Mode, DateTimeOffset ServerTime,
    DateTimeOffset ExpiresAt, double RemainingSeconds, string SessionToken);

public sealed class SessionHeartbeats(ServerDbContext db, LearnerSessions sessions, SessionTokens tokens, TimeProvider time)
{
    public async Task<SessionOperation> BeatAsync(Guid id, string? token, HeartbeatRequest request, CancellationToken ct)
    {
        if (request.ClientElapsedMilliseconds is < 0 or > 86_400_000)
            return new(400, new SessionError("invalid_elapsed", "Invalid elapsed time."));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await sessions.FindAsync(token, false, ct);
        if (found == null || found.Value.Session.Id != id)
        { await transaction.CommitAsync(ct); return LearnerSessions.Invalid(); }
        var (session, account) = found.Value;
        if (session.Mode == null) return new(409, new SessionError("mode_required", "Select a mode before heartbeat."));
        var now = time.GetUtcNow();
        bool previousContact = await db.Heartbeats.AnyAsync(h => h.SessionId == id, ct);
        var gap = SessionTiming.Gap(session, now, previousContact && request.WasOffline);
        if (previousContact && request.WasOffline)
        {
            // POSTs can reach the server while their responses are lost. Do not infer connectivity from receipt alone.
            var lastOnline = await db.Heartbeats.Where(h => h.SessionId == id && !h.WasOffline)
                .OrderByDescending(h => h.ReceivedAt).Select(h => (DateTimeOffset?)h.ReceivedAt).FirstOrDefaultAsync(ct);
            if (lastOnline != null)
            {
                var ongoing = await db.OfflineGaps.Where(g => g.SessionId == id && g.Kind == "Network" && g.StartedAt >= lastOnline)
                    .OrderByDescending(g => g.StartedAt).FirstOrDefaultAsync(ct);
                if (ongoing != null) { ongoing.EndedAt = now; gap = ongoing; }
                else if (gap == null && lastOnline.Value.AddSeconds(30) <= now)
                    gap = new SessionOfflineGap { SessionId = id, Kind = "Network", StartedAt = lastOnline.Value.AddSeconds(30), EndedAt = now };
            }
        }
        if (gap != null && db.Entry(gap).State == EntityState.Detached) db.OfflineGaps.Add(gap);
        if (session.Mode == "Testing" && request.ClientElapsedMilliseconds is { } elapsed)
        {
            var gaps = await db.OfflineGaps.AsNoTracking().Where(g => g.SessionId == id).ToListAsync(ct);
            if (gap != null) { gaps.RemoveAll(g => gap.Id != 0 && g.Id == gap.Id); gaps.Add(gap); }
            bool first = session.TestingClockStartedAt == null;
            bool flagged = first
                ? elapsed > (now - session.ModeSelectedAt!.Value).TotalMilliseconds + SessionTiming.Tolerance.TotalMilliseconds
                : SessionTiming.IsInconsistent(session, now, elapsed, gaps);
            if (flagged && !session.TimingInconsistent)
            {
                session.TimingInconsistent = true;
                db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "TestingTimingFlagged", CreatedAt = now,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { sessionId = id, clientElapsedMilliseconds = elapsed }) });
            }
            // The first observed running exam anchors timing diagnostics; rules-screen waiting is not exam time.
            session.TestingClockStartedAt ??= now;
            session.TestingClockBaselineMilliseconds ??= elapsed;
        }
        db.Heartbeats.Add(new SessionHeartbeat { SessionId = id, ReceivedAt = now,
            ClientElapsedMilliseconds = request.ClientElapsedMilliseconds, WasOffline = request.WasOffline });
        session.LastHeartbeatAt = now;
        await db.SaveChangesAsync(ct);
        var expiresAt = SessionTiming.Deadline(session);
        var reply = new HeartbeatReply(id, session.Mode, now, expiresAt,
            Math.Max(0, (expiresAt - now).TotalSeconds), tokens.Issue(id, account.CredentialVersion));
        await transaction.CommitAsync(ct);
        return new(200, reply);
    }
}
