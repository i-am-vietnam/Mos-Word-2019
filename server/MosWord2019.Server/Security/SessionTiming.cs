using MosWord2019.Server.Data;

namespace MosWord2019.Server.Security;

public static class SessionTiming
{
    public static readonly TimeSpan ContactTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(90);

    public static DateTimeOffset Deadline(Session session) => session.Mode switch
    {
        "Training" => session.StartedAt.AddHours(4),
        "Testing" when session.ModeSelectedAt != null => session.ModeSelectedAt.Value.AddHours(2),
        _ => session.StartedAt.AddMinutes(15)
    };

    // Missing contact is an observation, not proof of a network pause. Crash downtime must still count.
    public static SessionOfflineGap? Gap(Session session, DateTimeOffset now, bool network)
    {
        var start = (session.LastHeartbeatAt ?? session.StartedAt).Add(ContactTimeout);
        var end = now < Deadline(session) ? now : Deadline(session);
        if (session.Mode == null || end <= start) return null;
        return new SessionOfflineGap { SessionId = session.Id, StartedAt = start, EndedAt = end,
            Kind = network ? "Network" : "Unobserved" };
    }

    public static bool IsInconsistent(Session session, DateTimeOffset observedAt, long clientElapsedMilliseconds,
        IEnumerable<SessionOfflineGap> gaps)
    {
        if (session.Mode != "Testing" || session.ModeSelectedAt == null) return false;
        var start = session.TestingClockStartedAt ?? session.ModeSelectedAt.Value;
        if (observedAt < start || clientElapsedMilliseconds < 0) return true;
        // Union clipped intervals, including the offline period still open at finalization in C4.
        var intervals = gaps.Where(g => g.Kind == "Network" && g.EndedAt >= start && g.StartedAt <= observedAt)
            .Select(g => (Start: g.StartedAt < start ? start : g.StartedAt, End: g.EndedAt > observedAt ? observedAt : g.EndedAt))
            .OrderBy(g => g.Start).ToList();
        var covered = TimeSpan.Zero;
        var cursor = start;
        DateTimeOffset? episodeEnd = null;
        int episodes = 0;
        foreach (var interval in intervals)
        {
            if (episodeEnd == null || interval.Start > episodeEnd.Value) episodes++;
            if (episodeEnd == null || interval.End > episodeEnd.Value) episodeEnd = interval.End;
            var from = interval.Start > cursor ? interval.Start : cursor;
            if (interval.End > from) covered += interval.End - from;
            if (interval.End > cursor) cursor = interval.End;
        }
        var expected = observedAt - start - covered;
        // Each inferred outage has its own contact/response uncertainty; repeated short losses must not accumulate a false flag.
        double uncertainty = Tolerance.TotalMilliseconds * (1L + episodes);
        return Math.Abs(clientElapsedMilliseconds - expected.TotalMilliseconds -
            (session.TestingClockBaselineMilliseconds ?? 0)) > uncertainty;
    }
}
