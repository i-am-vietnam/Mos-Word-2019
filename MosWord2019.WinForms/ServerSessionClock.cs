using System;
using System.Diagnostics;
using MosWord2019.Core.Interfaces;

namespace MosWord2019
{
    // Online UTC comes only from authenticated server time plus a monotonic interval.
    internal sealed class ServerSessionClock : ITestingClock
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly DateTimeOffset anchor;
        private readonly Func<DateTimeOffset?> projected;
        private readonly DateTimeOffset? ceiling;
        private readonly object gate = new object();
        private DateTime latest;
        internal ServerSessionClock(DateTimeOffset serverTime, Func<DateTimeOffset?> projected = null, DateTimeOffset? ceiling = null)
        {
            anchor = serverTime; this.projected = projected; this.ceiling = ceiling;
            latest = (ceiling.HasValue && serverTime > ceiling.Value ? ceiling.Value : serverTime).UtcDateTime;
        }
        public TimeSpan Monotonic => watch.Elapsed;
        public DateTime UtcNow
        {
            get
            {
                var current = projected?.Invoke() ?? anchor.Add(watch.Elapsed);
                if (ceiling.HasValue && current > ceiling.Value) current = ceiling.Value;
                lock (gate) { if (current.UtcDateTime > latest) latest = current.UtcDateTime; return latest; }
            }
        }
    }
}
