using System;
using MosWord2019.Core.Models;

namespace MosWord2019.Word
{
    internal interface IRecoveredWordProcess : IDisposable
    {
        bool HasExited { get; }
        OwnedWordProcessIdentity ReadIdentity();
        void Terminate();
    }
    public sealed class OwnedWordProcessRecovery
    {
        private readonly Func<int, IRecoveredWordProcess> open;
        private readonly Action<string> log;
        public OwnedWordProcessRecovery() : this(WinApiProcessHelper.OpenRecoveryHandle) { }
        internal OwnedWordProcessRecovery(Func<int, IRecoveredWordProcess> open, Action<string> log = null)
        { this.open = open; this.log = log ?? (s => MosWord2019.Core.Diagnostics.AppLogger.Write(s)); }
        public bool IsRunning(OwnedWordProcessIdentity expected, string recordedMachine)
        {
            if (expected == null || !expected.IsWord || !string.Equals(recordedMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using (var process = open(expected.ProcessId))
                    return process != null && !process.HasExited && expected.Matches(process.ReadIdentity());
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException))
            { log("Word ownership not proven for PID " + expected.ProcessId + ": " + ex.Message); return false; }
        }
        public bool Stop(OwnedWordProcessIdentity expected, string recordedMachine)
        {
            if (expected == null || !expected.IsWord || !string.Equals(recordedMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using (var process = open(expected.ProcessId))
                {
                    // Exit, both identity observations and termination use the same kernel handle.
                    if (process == null || process.HasExited || !expected.Matches(process.ReadIdentity()) ||
                        process.HasExited || !expected.Matches(process.ReadIdentity()) || process.HasExited) return false;
                    process.Terminate(); return true;
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException))
            { log("Word stop refused for PID " + expected.ProcessId + ": " + ex.Message); return false; }
        }
    }
}
