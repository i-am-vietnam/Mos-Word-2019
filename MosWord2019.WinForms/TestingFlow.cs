using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using System.Threading;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
namespace MosWord2019
{
    internal static class TestingFlow
    {
        internal static IReadOnlyList<ProjectPackage> LoadForExam(string root, PreparedTestingSession prepared)
            => new ProjectLoader().LoadAll(root, prepared.Language);
        internal static void Open(IWin32Window owner, string language, OnlineSessionContext online = null)
        {
            try
            {
                if (online != null) { OpenOnline(owner, language, online); return; }
                do
                {
                    var store = new TestingSessionStateStore();
                    if (Program.PendingTestingRecovery == null && File.Exists(store.StatePath))
                    {
                        if (!Program.InspectTestingRecovery(owner)) return;
                        // Later/refused orphan close is a decision to keep pending, never permission to Prepare.
                        if (Program.PendingTestingRecovery == null && store.RequiresRecovery) return;
                    }
                    var record = Program.TakeTestingRecovery();
                    string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects");
                    var available = new ProjectLoader().LoadAll(root, language);
                    PreparedTestingSession prepared;
                    try { prepared = record?.Prepared ?? store.Prepare(Path.Combine(root, "testing-catalog.json"), available, language); }
                    catch (IOException ex) when (File.Exists(store.StatePath))
                    {
                        if (!Program.InspectTestingRecovery(owner, ex)) return;
                        if (Program.PendingTestingRecovery == null && store.RequiresRecovery) return;
                        continue;
                    }
                    var packages = LoadForExam(root, prepared); // The persisted language wins on resume/retry.
                    var host = TestingStaHost.Create(prepared, store, Program.Journal, packages, record);
                    TestingForm form;
                    try { form = new TestingForm(prepared, packages, host, record != null); }
                    catch { host.Dispose(); throw; }
                    using (form)
                    {
                        form.ShowDialog(owner);
                        if (!form.RecoveryRequired || Program.RecoveryDeferred) return;
                    }
                    if (!Program.InspectTestingRecovery(owner)) return;
                    if (Program.PendingTestingRecovery == null) return;
                } while (true);
            }
            catch (Exception ex)
            {
                AppLogger.Write("Testing flow retained pending state", ex);
                if (online != null && File.Exists(new TestingSessionStateStore().StatePath)) Program.DeferRecoveryToNextStart();
                if (Program.RecoveryDeferred) return;
                MessageBox.Show(owner, language == "vi" ? "Bài thi cần khôi phục; dữ liệu đã được giữ" : "Exam recovery is required; saved work is retained.",
                    "MOS Word 2019", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private static void OpenOnline(IWin32Window owner, string language, OnlineSessionContext online)
        {
            var store = new TestingSessionStateStore(utcNow: () => online.Clock.UtcNow);
            var packages = new ProjectLoader().LoadAll(online.ProjectsRootPath, language);
            var prepared = store.PrepareOnline(online.Packages, packages, language, online.Credentials.MachineId);
            TestingRecoveryRecord record = store.RequiresRecovery ? store.LoadOnline(online.Packages, packages) : null;
            long elapsedTicks = -1;
            online.ElapsedProvider = () => { long ticks = Interlocked.Read(ref elapsedTicks); return ticks < 0 ? (TimeSpan?)null : TimeSpan.FromTicks(ticks); };
            bool completed = false;
            var host = TestingStaHost.Create(prepared, store, Program.Journal, packages, record, online.Monitor,
                path => { online.RegisterResult(path); completed = true; }, sessionClock: online.Clock);
            host.StateChanged += snapshot => Interlocked.Exchange(ref elapsedTicks,
                snapshot.StartedUtc.HasValue ? snapshot.Elapsed.Ticks : -1);
            try
            {
                TestingForm form;
                try { form = new TestingForm(prepared, packages, host, record != null); }
                catch { host.Dispose(); throw; }
                using (form) form.ShowDialog(owner);
                if (completed && !File.Exists(store.StatePath)) online.EndAsync().GetAwaiter().GetResult();
                else if (File.Exists(store.StatePath) && !store.RequiresRecovery)
                {
                    store.DiscardPreparedOnline(online.Credentials.SessionId);
                    online.EndAsync().GetAwaiter().GetResult();
                }
            }
            finally
            {
                online.ElapsedProvider = null;
                if (File.Exists(store.StatePath) && store.RequiresRecovery) Program.DeferRecoveryToNextStart();
            }
        }
        // Server refusal never resumes Word or consumes local downtime as authorization.
        internal static void FinalizeOnlineSaved(OnlineSessionContext online)
        {
            var store = new TestingSessionStateStore(utcNow: () => online.Clock.UtcNow);
            var packages = store.RecoveryPackages;
            var record = store.LoadOnlineSaved();
            using (var host = TestingStaHost.Create(record.Prepared, store, Program.Journal, packages, record,
                online.Monitor, online.RegisterResult, enableWarmup: false, sessionClock: online.Clock)) host.SubmitSavedAsync().GetAwaiter().GetResult();
            if (File.Exists(store.StatePath)) throw new IOException("Completed exam cleanup is still pending; online recovery credentials must be retained.");
            online.EndAsync().GetAwaiter().GetResult();
        }
    }
}
