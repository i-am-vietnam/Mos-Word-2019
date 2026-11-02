using MosWord2019.Server.Data;

namespace MosWord2019.Server.Security;

public sealed record SessionPermission(bool Allowed, string? EndReason = null);

public static class SessionPolicy
{
    public static bool ValidIdentity(Account account, Session session, Guid credentialVersion) =>
        !account.IsAdmin && !account.Locked && credentialVersion == account.CredentialVersion &&
        session.AccountCredentialVersion == account.CredentialVersion;

    public static SessionPermission Evaluate(Account account, Session session, DateTimeOffset now,
        Guid credentialVersion)
    {
        if (credentialVersion != session.AccountCredentialVersion) return new(false);
        if (!ValidIdentity(account, session, credentialVersion))
            return new(false, session.EndedAt == null ? "Expired" : null);
        if (session.EndedAt != null) return new(false);
        if (account.ExpiresAt <= now || now < session.StartedAt) return new(false, "Expired");
        // Deadlines are exclusive; active heartbeat inactivity is inclusive at thirty-five minutes.
        if (session.Mode == null && now >= session.StartedAt.AddMinutes(15)) return new(false, "Expired");
        if (session.Mode == "Training" && now >= session.StartedAt.AddHours(4)) return new(false, "TimeLimit");
        if (session.Mode == "Testing" && (session.ModeSelectedAt == null ||
            now >= session.ModeSelectedAt.Value.AddHours(2))) return new(false, "TimeLimit");
        var lastSeen = session.LastHeartbeatAt ?? session.StartedAt;
        if (now < lastSeen || now - lastSeen > TimeSpan.FromMinutes(35)) return new(false, "Abandoned");
        return new(true);
    }

    // A restart has a shorter recovery window than a running client's temporary network outage.
    public static bool CanResume(Session session, DateTimeOffset now)
    {
        var lastSeen = session.LastHeartbeatAt ?? session.StartedAt;
        return now >= lastSeen && now - lastSeen <= TimeSpan.FromMinutes(15);
    }

    // Offline upload is a separate permission and never grants active-session access.
    public static bool CanUpload(Account account, Session session, DateTimeOffset now,
        Guid credentialVersion, DateTimeOffset finalizedAt) =>
        ValidIdentity(account, session, credentialVersion) &&
        session.Mode == "Testing" && session.ModeSelectedAt is { } selected &&
        session.EndedAt is { } ended && finalizedAt >= selected && finalizedAt <= ended &&
        finalizedAt <= selected.AddHours(2) && now >= ended &&
        now <= (ended < selected.AddHours(2) ? ended : selected.AddHours(2)).AddHours(24);

    // Call under the session row lock. The caller saves and commits even on denied authorization.
    public static AuditEntry? End(Session session, DateTimeOffset now, string reason)
    {
        if (session.EndedAt != null) return null;
        session.EndedAt = now < session.StartedAt ? session.StartedAt : now;
        session.EndReason = reason;
        return new AuditEntry { AccountId = session.AccountId, Action = "SessionEnded", CreatedAt = now,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { sessionId = session.Id, reason }) };
    }
}
