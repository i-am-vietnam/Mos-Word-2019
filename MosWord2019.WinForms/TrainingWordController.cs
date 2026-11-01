using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Word;
namespace MosWord2019
{
    /// <summary>Owns the Training controller on one pumped STA. No COM reference crosses this boundary.</summary>
    internal sealed class TrainingWordController : IWordController, IWordDocumentState, IWordSaveFormat,
        IWordWindowLayout, IWordExportCheckpoint, IWordViewState, IWordTocLayout, IWordSaveEvents
    {
        private readonly Control callbacks = new Control();
        private readonly Thread thread;
        private readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<Action> queue = new Queue<Action>();
        private Control dispatcher;
        private IWordController controller;
        private bool executing, disposed, hadDocument;
        private int waiting;
        internal event Action Idle;
        internal bool IsBusy => Volatile.Read(ref waiting) > 0;
        internal WordWarmup Warmup { get; }
        public event Action<string> OwnedDocumentSaving;
        public event Action<string> WorkingFileWritten;
        public event Action<string> ExportCheckpointFailed;
        internal TrainingWordController(Func<IWordController> create = null, Func<IWordController, bool> alive = null)
        {
            callbacks.CreateControl();
            thread = new Thread(() =>
            {
                using (var control = new Control())
                {
                    try
                    {
                        dispatcher = control; control.CreateControl(); controller = (create ?? (() => new WordController()))();
                        if (controller is IWordSaveEvents saves)
                        {
                            saves.OwnedDocumentSaving += path => Callback(() => OwnedDocumentSaving?.Invoke(path));
                            saves.WorkingFileWritten += path => Callback(() => WorkingFileWritten?.Invoke(path));
                        }
                        if (controller is IWordExportCheckpoint checkpoint) checkpoint.ExportCheckpointFailed += text => Callback(() => ExportCheckpointFailed?.Invoke(text));
                        ready.TrySetResult(true); Application.Run();
                    }
                    catch (Exception ex) { ready.TrySetException(ex); }
                    finally { stopped.TrySetResult(true); }
                }
            }) { IsBackground = true, Name = "Training Word STA" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Warmup = new WordWarmup(() => InvokeAsync(w => { if (w is WordController native) native.Prewarm(); else w.StartWord(); return true; }), () => InvokeAsync(w =>
            {
                using ((w as WordController)?.BeginShortCallBudget(250))
                {
                    if (hadDocument && !w.IsOpened) { w.ReleaseClosedApplication(); hadDocument = false; return false; }
                }
                return alive != null ? alive(w) : ((WordController)w).IsApplicationRunning;
            }));
        }
        private void Callback(Action action)
        {
            // Ownership updates finish synchronously on the UI before native save continues.
            if (callbacks.IsDisposed) return;
            callbacks.Invoke(action);
        }
        private void Drain()
        {
            if (executing) return; executing = true;
            try { while (queue.Count > 0) queue.Dequeue()(); }
            finally { executing = false; }
        }
        internal async Task<T> InvokeAsync<T>(Func<IWordController, T> action)
        {
            await ready.Task.ConfigureAwait(false);
            if (disposed) throw new ObjectDisposedException(nameof(TrainingWordController));
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke((Action)(() =>
            {
                queue.Enqueue(() => { try { done.TrySetResult(action(controller)); } catch (Exception ex) { done.TrySetException(ex); } }); Drain();
            }));
            if (await Task.WhenAny(done.Task, stopped.Task).ConfigureAwait(false) != done.Task) throw new InvalidOperationException("Training Word STA stopped.");
            return await done.Task.ConfigureAwait(false);
        }
        internal static T Wait<T>(Task<T> task)
        {
            // The form gates operations/close/monitor while this nested pump paints and dispatches save callbacks.
            while (!task.IsCompleted) { Application.DoEvents(); ((IAsyncResult)task).AsyncWaitHandle.WaitOne(10); }
            return task.GetAwaiter().GetResult();
        }
        internal void JoinWarmup()
        {
            try { Wait(Warmup.EnsureAsync().ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default)); }
            catch (Exception ex) { MosWord2019.Core.Diagnostics.AppLogger.Write("Training prewarm unavailable; ordinary open will retry: " + ex.Message); }
        }
        private T Invoke<T>(Func<IWordController, T> action)
        {
            if (Thread.CurrentThread == thread) return action(controller);
            Interlocked.Increment(ref waiting);
            try { return Wait(InvokeAsync(action)); }
            finally { if (Interlocked.Decrement(ref waiting) == 0) Idle?.Invoke(); }
        }
        private void Invoke(Action<IWordController> action) => Invoke(w => { action(w); return true; });
        public bool IsOpened => Invoke(w => w.IsOpened);
        public string CurrentDocumentPath => Invoke(w => ((IWordDocumentState)w).CurrentDocumentPath);
        public string DefaultTemplateFolder => Invoke(w => ((IWordDocumentState)w).DefaultTemplateFolder);
        public int CurrentSaveFormat => Invoke(w => ((IWordSaveFormat)w).CurrentSaveFormat);
        internal string OpenTiming => Invoke(w => (w as WordController)?.OpenTiming ?? "fakeController=true");
        public void StartWord() => Invoke(w => w.StartWord());
        public void ReleaseClosedApplication() => Invoke(w => { w.ReleaseClosedApplication(); hadDocument = false; });
        public void OpenDocument(string path) => Invoke(w => { w.OpenDocument(path); hadDocument = true; Warmup.ObserveReady(); });
        public void Save() => Invoke(w => w.Save());
        public void CloseDocument() => Invoke(w => { w.CloseDocument(); hadDocument = false; });
        public void Close() => Invoke(w => { w.Close(); hadDocument = false; });
        public void SetWindowBounds(int left, int top, int width, int height) => Invoke(w => ((IWordWindowLayout)w).SetWindowBounds(left, top, width, height));
        public void ConfigureExportCheckpoint(string path) => Invoke(w => ((IWordExportCheckpoint)w).ConfigureExportCheckpoint(path));
        public string SaveFormattedCheckpoint() => Invoke(w => ((IWordExportCheckpoint)w).SaveFormattedCheckpoint());
        public WordFormattingMarksSnapshot ReadFormattingMarks() => Invoke(w => ((IWordViewState)w).ReadFormattingMarks());
        public WordTocLayoutSnapshot ReadTocLayout() => Invoke(w => ((IWordTocLayout)w).ReadTocLayout());
        internal IDisposable BeginShortCallBudget(int milliseconds)
        {
            var budget = Invoke(w => (w as WordController)?.BeginShortCallBudget(milliseconds));
            return new Budget(() => Invoke(w => budget?.Dispose()));
        }
        private sealed class Budget : IDisposable
        {
            private readonly Action dispose; internal Budget(Action dispose) { this.dispose = dispose; } public void Dispose() { dispose(); }
        }
        public void Dispose()
        {
            if (disposed) return;
            Invoke(w => w.Dispose()); // A failure leaves the same owner/dispatcher available for the existing Retry path.
            disposed = true;
            dispatcher.BeginInvoke((Action)(() => Application.ExitThread()));
            if (Thread.CurrentThread != thread) thread.Join(2000);
            callbacks.Dispose();
        }
    }
}
