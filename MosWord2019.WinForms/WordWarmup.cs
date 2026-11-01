using System;
using System.Threading;
using System.Threading.Tasks;
namespace MosWord2019
{
    internal enum WordWarmupState { NotStarted, Warming, Ready, Failed }
    /// <summary>Coalesces speculative activation; failure never replaces the ordinary open path.</summary>
    internal sealed class WordWarmup
    {
        private readonly object gate = new object();
        private readonly Func<Task> start;
        private readonly Func<Task<bool>> alive;
        private Task pending;
        private WordWarmupState state;
        private int checking;
        internal WordWarmup(Func<Task> start, Func<Task<bool>> alive) { this.start = start; this.alive = alive; }
        internal event Action<WordWarmupState> StateChanged;
        internal WordWarmupState State { get { lock (gate) return state; } }
        internal Task EnsureAsync()
        {
            TaskCompletionSource<bool> completion;
            lock (gate)
            {
                if (state != WordWarmupState.NotStarted) return pending ?? Task.CompletedTask;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = completion.Task; state = WordWarmupState.Warming;
            }
            StateChanged?.Invoke(WordWarmupState.Warming);
            _ = Activate(completion); return completion.Task;
        }
        private async Task Activate(TaskCompletionSource<bool> completion)
        {
            try { await start().ConfigureAwait(false); lock (gate) state = WordWarmupState.Ready; StateChanged?.Invoke(WordWarmupState.Ready); completion.TrySetResult(true); }
            catch (Exception ex) { lock (gate) state = WordWarmupState.Failed; StateChanged?.Invoke(WordWarmupState.Failed); completion.TrySetException(ex); }
        }
        internal void ObserveReady()
        {
            lock (gate) { state = WordWarmupState.Ready; pending = Task.CompletedTask; }
        }
        internal async Task CheckAsync()
        {
            if (State != WordWarmupState.Ready || Interlocked.Exchange(ref checking, 1) != 0) return;
            try
            {
                if (!await alive().ConfigureAwait(false))
                {
                    lock (gate) { if (state == WordWarmupState.Ready) state = WordWarmupState.NotStarted; }
                    await EnsureAsync().ConfigureAwait(false);
                }
            }
            finally { Volatile.Write(ref checking, 0); }
        }
    }
}
