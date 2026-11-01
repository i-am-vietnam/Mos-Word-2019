using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
namespace MosWord2019.Core.Diagnostics
{
    /// <summary>Monotonic per-phase diagnostics; never controls application behavior.</summary>
    public sealed class OperationTimings
    {
        private readonly Stopwatch total = Stopwatch.StartNew();
        private readonly Dictionary<string, long> phases = new Dictionary<string, long>();
        public void Measure(string phase, Action action)
        {
            var clock = Stopwatch.StartNew();
            try { action(); } finally { phases[phase] = clock.ElapsedMilliseconds; }
        }
        public override string ToString() => string.Join(" ", phases.Select(p => p.Key + "Ms=" + p.Value)) + " totalMs=" + total.ElapsedMilliseconds;
    }
}
