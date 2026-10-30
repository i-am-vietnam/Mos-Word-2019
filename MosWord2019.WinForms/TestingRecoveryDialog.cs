using System;
using System.Drawing;
using System.Windows.Forms;

namespace MosWord2019
{
    internal enum TestingRecoveryFailureChoice { Retry, SubmitSaved, Later }

    internal static class TestingRecoveryDialog
    {
        internal static bool Run(Action inspect, Action finalizeSaved, Func<Exception, TestingRecoveryFailureChoice> choose,
            Action<Exception> log, Exception initialFailure = null)
        {
            Action action = inspect;
            Exception failure = initialFailure;
            while (true)
            {
                if (failure != null)
                {
                    log(failure);
                    var choice = choose(failure);
                    if (choice == TestingRecoveryFailureChoice.Later) return false;
                    action = choice == TestingRecoveryFailureChoice.SubmitSaved ? finalizeSaved : inspect;
                }
                try { action(); return true; }
                catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException)) { failure = ex; }
            }
        }

        internal static TestingRecoveryFailureChoice Show(IWin32Window owner)
        {
            var choice = TestingRecoveryFailureChoice.Later;
            using (var form = Create(choiceValue => choice = choiceValue)) form.ShowDialog(owner);
            return choice;
        }

        internal static Form Create(Action<TestingRecoveryFailureChoice> select)
        {
            var form = new Form { Text = "Khôi phục bài thi / Exam recovery", Name = "TestingRecoveryChoices", ClientSize = new Size(760, 200),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            form.Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(16), Text =
                "Không thể khôi phục bài thi. Dữ liệu đã lưu được giữ nguyên. Hãy thử lại hoặc nộp bài từ autosave/checkpoint đã lưu.\n\n" +
                "Exam recovery failed. Saved data is retained. Retry, or submit from saved autosaves/checkpoints." });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(8) };
            Action<string, string, TestingRecoveryFailureChoice> add = (name, text, value) =>
            {
                var button = new Button { Name = name, Text = text, Size = new Size(name == "SubmitSaved" ? 355 : 175, 48) };
                button.Click += (s, e) => { select(value); form.Close(); };
                buttons.Controls.Add(button);
                if (value == TestingRecoveryFailureChoice.Later) form.CancelButton = button;
            };
            add("Retry", "Thử lại / Retry", TestingRecoveryFailureChoice.Retry);
            add("SubmitSaved", "Nộp bài từ dữ liệu đã lưu / Submit from saved work", TestingRecoveryFailureChoice.SubmitSaved);
            add("Later", "Để sau / Later", TestingRecoveryFailureChoice.Later);
            form.Controls.Add(buttons); buttons.BringToFront();
            return form;
        }
    }
}
