using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
namespace MosWord2019
{
    internal static class TestingFlow
    {
        internal static IReadOnlyList<ProjectPackage> LoadForExam(string root, PreparedTestingSession prepared)
            => new ProjectLoader().LoadAll(root, prepared.Language);
        internal static void Open(IWin32Window owner, string language)
        {
            try
            {
                do
                {
                    var store = new TestingSessionStateStore();
                    if (Program.PendingTestingRecovery == null && File.Exists(store.StatePath)) Program.InspectTestingRecovery();
                    var record = Program.TakeTestingRecovery();
                    string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects");
                    var available = new ProjectLoader().LoadAll(root, language);
                    var prepared = record?.Prepared ?? store.Prepare(Path.Combine(root, "testing-catalog.json"), available, language);
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
                    Program.InspectTestingRecovery();
                } while (Program.PendingTestingRecovery != null);
            }
            catch (Exception ex)
            {
                AppLogger.Write("Testing flow retained pending state", ex);
                if (Program.RecoveryDeferred) return;
                MessageBox.Show(owner, language == "vi" ? "Bài thi cần khôi phục; dữ liệu đã được giữ" : "Exam recovery is required; saved work is retained.",
                    "MOS Word 2019", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
