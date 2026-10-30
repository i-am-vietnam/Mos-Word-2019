using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace MosWord2019
{
    internal sealed class GradingProgress : Form
    {
        internal GradingProgress(string language)
        {
            Text = language == "vi" ? "Đang chấm bài…" : "Grading…";
            ControlBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; ClientSize = new Size(360, 100);
            Controls.Add(new Label { Text = Text, Dock = DockStyle.Top, Height = 42, TextAlign = ContentAlignment.MiddleCenter });
            Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Dock = DockStyle.Bottom, Height = 24 });
        }
        internal static T Run<T>(string language, Func<Task<T>> work, IWin32Window owner = null)
        {
            T value = default(T); Exception failure = null;
            using (var window = new GradingProgress(language))
            {
                bool finished = false;
                window.FormClosing += (s, e) =>
                {
                    if (finished) return;
                    if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing) Program.DeferRecoveryToNextStart();
                    else e.Cancel = true;
                };
                window.Shown += async (s, e) => { try { value = await work(); } catch (Exception ex) { failure = ex; } finally { finished = true; if (!window.IsDisposed) window.Close(); } };
                window.ShowDialog(owner); // Nested message pump remains responsive while the STA host grades.
                if (!finished) throw new OperationCanceledException("Grading interrupted; saved state retained.");
            }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return value;
        }
    }
}
