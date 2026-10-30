using System;
using System.Diagnostics;

namespace MosWord2019.Core.Interfaces
{
    public interface ITestingClock
    {
        TimeSpan Monotonic { get; }
        DateTime UtcNow { get; }
    }
    public interface IConnectivityMonitor
    {
        bool IsOnline { get; }
        event Action<bool> ConnectivityChanged;
    }
    public sealed class StopwatchTestingClock : ITestingClock
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public TimeSpan Monotonic => watch.Elapsed;
        public DateTime UtcNow => DateTime.UtcNow;
    }
    public sealed class AlwaysOnlineMonitor : IConnectivityMonitor
    {
        public bool IsOnline => true;
        public event Action<bool> ConnectivityChanged { add { } remove { } }
    }
}
