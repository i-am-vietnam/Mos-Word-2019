using System;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public enum TestingRecoveryAction { StartPrepared, Resume, Finalize, ArchiveCompleted }
    public sealed class TestingRecoveryDecision
    {
        public TestingRecoveryAction Action { get; }
        public TimeSpan Elapsed { get; }
        public TimeSpan Downtime { get; }
        public TimeSpan Remaining => TestingSession.Duration - Elapsed;
        private TestingRecoveryDecision(TestingRecoveryAction action, TimeSpan elapsed, TimeSpan downtime)
        { Action = action; Elapsed = elapsed; Downtime = downtime; }
        public static TestingRecoveryDecision Evaluate(TestingSessionStatus status, TimeSpan storedElapsed,
            DateTime heartbeatUtc, DateTime nowUtc, bool sameMachine)
        {
            if (!Enum.IsDefined(typeof(TestingSessionStatus), status) || storedElapsed < TimeSpan.Zero || storedElapsed > TestingSession.Duration ||
                nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Invalid Testing recovery timing.");
            if (status == TestingSessionStatus.Ready) return new TestingRecoveryDecision(TestingRecoveryAction.StartPrepared, TimeSpan.Zero, TimeSpan.Zero);
            if (status == TestingSessionStatus.Completed) return new TestingRecoveryDecision(TestingRecoveryAction.ArchiveCompleted, storedElapsed, TimeSpan.Zero);
            if (heartbeatUtc.Kind != DateTimeKind.Utc || heartbeatUtc == default(DateTime)) throw new ArgumentException("A UTC heartbeat is required.");
            TimeSpan downtime = nowUtc > heartbeatUtc ? nowUtc - heartbeatUtc : TimeSpan.Zero;
            long ticks = Math.Min(TestingSession.Duration.Ticks, storedElapsed.Ticks + Math.Min(downtime.Ticks, TestingSession.Duration.Ticks));
            bool resume = sameMachine && downtime <= TimeSpan.FromMinutes(15) && ticks < TestingSession.Duration.Ticks &&
                (status == TestingSessionStatus.Running || status == TestingSessionStatus.Offline);
            return new TestingRecoveryDecision(resume ? TestingRecoveryAction.Resume : TestingRecoveryAction.Finalize, TimeSpan.FromTicks(ticks), downtime);
        }
    }
}
