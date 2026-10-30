#if DEBUG
using System;
using MosWord2019.Core.Interfaces;

namespace MosWord2019
{
    /// <summary>Per-exam manual simulation; excluded entirely from Release builds.</summary>
    internal sealed class DebugConnectivityMonitor : IConnectivityMonitor
    {
        private volatile bool online = true;
        public bool IsOnline => online;
        public event Action<bool> ConnectivityChanged;

        internal void SetOnline(bool value)
        {
            if (online == value) return;
            online = value;
            ConnectivityChanged?.Invoke(value);
        }
    }
}
#endif
