using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

public sealed class SessionTimingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
    private static Session State() => new() { Mode = "Testing", StartedAt = Start, ModeSelectedAt = Start,
        TestingClockStartedAt = Start, TestingClockBaselineMilliseconds = 0 };

    [Fact]
    public void NetworkIntervalsAreClippedAndUnionedWhileUnobservedCrashTimeCounts()
    {
        var session = State();
        var gaps = new[]
        {
            new SessionOfflineGap { StartedAt = Start.AddMinutes(-1), EndedAt = Start.AddMinutes(2), Kind = "Network" },
            new SessionOfflineGap { StartedAt = Start.AddMinutes(1), EndedAt = Start.AddMinutes(3), Kind = "Network" },
            new SessionOfflineGap { StartedAt = Start.AddMinutes(8), EndedAt = Start.AddMinutes(12), Kind = "Network" },
            new SessionOfflineGap { StartedAt = Start.AddMinutes(4), EndedAt = Start.AddMinutes(7), Kind = "Unobserved" }
        };
        Assert.False(SessionTiming.IsInconsistent(session, Start.AddMinutes(10), 300_000, gaps));
        Assert.True(SessionTiming.IsInconsistent(session, Start.AddMinutes(10), 0, gaps));
    }

    [Fact]
    public void InferredOutagesHaveSeparateUncertaintyWithoutHidingLargeTimingDrift()
    {
        var session = State();
        var gaps = Enumerable.Range(1, 5).Select(i => new SessionOfflineGap
        { StartedAt = Start.AddMinutes(i * 2), EndedAt = Start.AddMinutes(i * 2), Kind = "Network" }).ToArray();
        Assert.False(SessionTiming.IsInconsistent(session, Start.AddMinutes(12), 620_000, gaps));
        Assert.True(SessionTiming.IsInconsistent(session, Start.AddMinutes(12), 0, gaps));
    }

    [Fact]
    public void TimingToleranceAndFirstObservedBaselineAreExplicit()
    {
        var session = State(); session.TestingClockBaselineMilliseconds = 30_000;
        Assert.False(SessionTiming.IsInconsistent(session, Start.AddMinutes(5), 420_000, []));
        Assert.True(SessionTiming.IsInconsistent(session, Start.AddMinutes(5), 420_001, []));
        Assert.True(SessionTiming.IsInconsistent(session, Start.AddSeconds(-1), 0, []));
        Assert.True(SessionTiming.IsInconsistent(session, Start, -1, []));
        session.Mode = "Training";
        Assert.False(SessionTiming.IsInconsistent(session, Start.AddMinutes(5), 0, []));
    }
}
