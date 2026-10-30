using System;
using MosWord2019.Core.Models;

namespace MosWord2019.Word
{
    internal interface IRecoveredWordProcess : IDisposable
    {
        OwnedWordProcessIdentity ReadIdentity();
        void Terminate();
    }
    public sealed class OwnedWordProcessRecovery
    {
        private readonly Func<int, IRecoveredWordProcess> open;
        public OwnedWordProcessRecovery() : this(WinApiProcessHelper.OpenRecoveryHandle) { }
        internal OwnedWordProcessRecovery(Func<int, IRecoveredWordProcess> open) { this.open = open; }
        public bool Stop(OwnedWordProcessIdentity expected, string recordedMachine)
        {
            if (expected == null || !expected.IsWord || !string.Equals(recordedMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
            using (var process = open(expected.ProcessId))
            {
                // Both observations and termination use this same kernel handle, never a second PID lookup.
                if (process == null || !expected.Matches(process.ReadIdentity()) || !expected.Matches(process.ReadIdentity())) return false;
                process.Terminate();
                return true;
            }
        }
    }
}
