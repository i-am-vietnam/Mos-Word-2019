using System;
using System.Linq;
using System.IO;
using MosWord2019.Word;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Projects;

namespace MosWord2019
{
    internal static class Program
    {
        internal static SessionRecoveryJournal Journal { get; private set; }
        internal static TrainingWorkspaceService Workspace { get; private set; }
        internal static TestingRecoveryRecord PendingTestingRecovery { get; private set; }
        private static Control dispatcher;
        private static int uiThread;
        private static int handlingFatal;
        private static int recoveryDeferred;
        internal static bool RecoveryDeferred => Volatile.Read(ref recoveryDeferred) != 0;
        internal static void DeferRecoveryToNextStart() { Interlocked.Exchange(ref recoveryDeferred, 1); }

        [STAThread]
        private static void Main()
        {
            using (var instance = new TrainerInstanceLock())
            {
                if (!instance.Acquired)
                {
                    MessageBox.Show("MOS Word 2019 Trainer is already running. / MOS Word 2019 Trainer đang chạy.",
                        "MOS Word 2019", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // The instance lock must precede recovery: never recover another live trainer's files.
                Journal = new SessionRecoveryJournal();
                new TestingSessionStateStore().ProtectPendingRecovery(Journal);
                Journal.Recover();
                Workspace = new TrainingWorkspaceService(Journal);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (sender, args) => HandleFatal(args.Exception);
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                    HandleFatal(args.ExceptionObject as Exception ?? new Exception("Unhandled non-Exception failure."));
                uiThread = Thread.CurrentThread.ManagedThreadId;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (dispatcher = new Control())
                {
                    dispatcher.CreateControl();
                    try
                    {
                        InspectTestingRecovery();
                        if (PendingTestingRecovery != null) TestingFlow.Open(null, PendingTestingRecovery.Prepared.Language);
                        if (!RecoveryDeferred) Application.Run(new LoginForm());
                    }
                    catch (Exception ex) { HandleFatal(ex); }
                    finally
                    {
                        if (!RecoveryDeferred) { Journal.Recover(); Workspace.CleanupWorkingFiles(); }
                        else AppLogger.Write("Shutdown left journal intact for recovery at next startup.");
                    }
                }
            }
        }

        internal static TestingRecoveryRecord TakeTestingRecovery()
        { var record = PendingTestingRecovery; PendingTestingRecovery = null; return record; }

        internal static bool InspectTestingRecovery(IWin32Window owner = null, Exception initialFailure = null)
        {
            var store = new TestingSessionStateStore();
            PendingTestingRecovery = null;
            if (!File.Exists(store.StatePath) && initialFailure == null) return true;
            string projects = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects");
            return TestingRecoveryDialog.Run(
                () => InspectTestingRecoveryCore(owner, store, projects),
                () =>
                {
                    var packages = new ProjectLoader().LoadAll(projects, "en");
                    new TestingStartupRecovery(store, Journal).FinalizeSaved(Path.Combine(projects, "testing-catalog.json"), packages,
                        record => FinalizeSavedRecord(owner, store, projects, record));
                },
                error => RecoveryDeferred ? TestingRecoveryFailureChoice.Later : TestingRecoveryDialog.Show(owner),
                error => { Journal.DeferRecovery(true); AppLogger.Write("Testing startup recovery retained pending session", error); }, initialFailure);
        }

        private static void InspectTestingRecoveryCore(IWin32Window owner, TestingSessionStateStore store, string projects)
        {
            var packages = new ProjectLoader().LoadAll(projects, "en");
            var process = new OwnedWordProcessRecovery();
            PendingTestingRecovery = new TestingStartupRecovery(store, Journal).Inspect(Path.Combine(projects, "testing-catalog.json"), packages,
                DateTime.UtcNow, process.IsRunning,
                id => MessageBox.Show(owner, "Word của bài thi trước vẫn đang mở và sẽ được đóng. Nếu bạn đang mở tài liệu cá nhân trong cửa sổ Word đó, hãy lưu lại rồi bấm Tiếp tục.\n\nWord from the previous exam is still open and will be closed. If you have personal documents open in that Word window, save them, then click Continue.",
                    "Tiếp tục / Continue", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK,
                process.Stop,
                record =>
                {
                    var choice = MessageBox.Show(owner, "Tiếp tục bài thi trước? Thời gian ứng dụng đóng vẫn được tính. Yes: tiếp tục; No: nộp bài; Cancel: để lần sau.\n\nResume the previous exam? Downtime counts. Yes: resume; No: submit; Cancel: keep pending.",
                        "MOS Word 2019", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    return choice == DialogResult.Yes ? TestingResumeChoice.Resume : choice == DialogResult.No ? TestingResumeChoice.Submit : TestingResumeChoice.Later;
                }, record => FinalizeSavedRecord(owner, store, projects, record));
        }

        private static void FinalizeSavedRecord(IWin32Window owner, TestingSessionStateStore store, string projects, TestingRecoveryRecord record)
        {
            var localized = TestingFlow.LoadForExam(projects, record.Prepared);
            using (var host = TestingStaHost.Create(record.Prepared, store, Journal, localized, record))
            {
                // Do not Resume/Start: saved-only submission never activates Word or reopens a document.
                var result = GradingProgress.Run(record.Prepared.Language, async () =>
                { await host.SubmitSavedAsync(); return await host.InvokeAsync(c => c.Session.Result); });
                using (var window = new ResultForm(result, localized, record.Prepared.Language)) window.ShowDialog(owner);
            }
        }

        private static void HandleFatal(Exception error)
        {
            AppLogger.Write("Fatal trainer error", error);
            if (Interlocked.Exchange(ref handlingFatal, 1) != 0) return;
            if (RecoveryDeferred) { Environment.Exit(1); return; }
            Action cleanup = () =>
            {
                if (!RecoveryDeferred) RunRecoveryCleanup(CloseOwnedForms, () => Workspace?.CleanupWorkingFiles(), () => Journal?.Recover(),
                    ex => AppLogger.Write("Fatal cleanup failed; retained journal for recovery", ex));
            };
            if (Thread.CurrentThread.ManagedThreadId == uiThread) cleanup();
            else
            {
                var finished = new TaskCompletionSource<bool>();
                try
                {
                    if (dispatcher == null || !dispatcher.IsHandleCreated) throw new InvalidOperationException("The owning STA is unavailable.");
                    dispatcher.BeginInvoke((Action)(() => { try { cleanup(); } finally { finished.TrySetResult(true); } }));
                    if (!finished.Task.Wait(2000)) throw new TimeoutException("The owning STA did not respond; no cross-thread Word calls were made.");
                }
                catch (Exception ex)
                {
                    AppLogger.Write("Owned Word cleanup could not run on its STA", ex);
                    try { if (!RecoveryDeferred) Journal?.Recover(); } catch (Exception recoveryError) { AppLogger.Write("Fatal journal recovery failed", recoveryError); }
                }
            }
            // The log and durable claims remain when a locked Word file cannot yet be recovered.
            Environment.Exit(1);
        }

        private static void CloseOwnedForms()
        {
            Exception failure = null;
            foreach (var form in Application.OpenForms.OfType<MainForm>().ToArray())
                try { form.CloseOwnedForRecovery(); }
                catch (Exception ex) { AppLogger.Write("Owned trainer cleanup failed", ex); failure = ex; }
            if (failure != null) throw new InvalidOperationException("Some owned resources remain for the next startup.", failure);
        }

        internal static void RunRecoveryCleanup(Action closeOwned, Action cleanupWorking, Action recover, Action<Exception> log)
        {
            FatalRecovery.Run(closeOwned,
                () => { if (!RecoveryDeferred) cleanupWorking(); },
                () => { if (!RecoveryDeferred) recover(); }, log);
        }
    }
}
