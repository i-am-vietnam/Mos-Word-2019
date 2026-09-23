using System;
using System.Drawing;
using System.Windows.Forms;

namespace MosWord2019
{
    public sealed class MainForm : Form
    {
        public MainForm(AppSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            Text = "MOS Word 2019 - " + session.Mode;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 420);
            MinimumSize = new Size(500, 300);
            AutoScaleMode = AutoScaleMode.Font;
            bool vietnamese = session.Language == "vi";
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, Text = "MOS Word 2019 | " + session.Mode, Font = new Font(Font.FontFamily, 18, FontStyle.Bold) });
            layout.Controls.Add(new Label {
                Name = "PhaseNotice", Dock = DockStyle.Fill, Padding = new Padding(0, 24, 0, 0),
                Text = vietnamese
                    ? "Chế độ này chưa khả dụng.\n\nChức năng luyện tập và kiểm tra sẽ được bổ sung sau."
                    : "This mode is not available yet.\n\nTraining and testing features will be added in later phases."
            });
            var back = new Button { Name = "Back", AutoSize = true, Text = vietnamese ? "Quay lại" : "Back" };
            back.Click += (sender, args) => Close();
            layout.Controls.Add(back);
            Controls.Add(layout);
            CancelButton = back;
        }
    }
}
