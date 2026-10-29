using System;
using System.Threading;

namespace MosWord2019.Projects
{
    /// <summary>The mutex is acquired before recovery and held through application shutdown.</summary>
    public sealed class TrainerInstanceLock : IDisposable
    {
        private readonly Mutex mutex;
        public TrainerInstanceLock(string name = @"Local\MosWord2019.Trainer")
        {
            mutex = new Mutex(false, name);
            try { Acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { Acquired = true; }
        }
        public bool Acquired { get; }
        public void Dispose()
        {
            if (Acquired) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    public static class FatalRecovery
    {
        // A failed close must not trigger a workspace sweep; journal recovery still uses exclusive handles.
        public static void Run(Action closeOwned, Action cleanupWorking, Action recover, Action<Exception> log)
        {
            bool closed = false;
            try { closeOwned(); closed = true; } catch (Exception ex) { log(ex); }
            if (closed) try { cleanupWorking(); } catch (Exception ex) { log(ex); }
            try { recover(); } catch (Exception ex) { log(ex); }
        }
    }
}
