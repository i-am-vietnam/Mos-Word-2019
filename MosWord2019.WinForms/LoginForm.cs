using System;
using System.Drawing;
using System.Windows.Forms;

namespace MosWord2019
{
    public sealed class LoginForm : Form
    {
        private readonly ComboBox mode = new ComboBox { Name = "Mode", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox language = new ComboBox { Name = "Language", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

        public LoginForm()
        {
            Text = "MOS Word 2019";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 285);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Font;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7 };
            layout.Controls.Add(new Label { Text = "MOS Word 2019", AutoSize = true, Font = new Font(Font.FontFamily, 18, FontStyle.Bold) });
            layout.Controls.Add(new Label { Text = "Training / Luyện tập", AutoSize = true });
            mode.Items.Add(AppMode.Training);
            mode.SelectedIndex = 0;
            layout.Controls.Add(mode);
            layout.Controls.Add(new Label { Text = "Language / Ngôn ngữ", AutoSize = true });
            language.Items.AddRange(new object[] { "English", "Tiếng Việt" });
            language.SelectedIndex = 0;
            layout.Controls.Add(language);
            layout.Controls.Add(new Label { Text = "Testing is not available. / Chưa có chế độ kiểm tra.", AutoSize = true, MaximumSize = new Size(400, 0) });
            var enter = new Button { Name = "Continue", Text = "Continue / Tiếp tục", AutoSize = true };
            enter.Click += OpenShell;
            layout.Controls.Add(enter);
            Controls.Add(layout);
            AcceptButton = enter;
        }

        private void OpenShell(object sender, EventArgs e)
        {
            var session = new AppSession(AppMode.Training, language.SelectedIndex == 1 ? "vi" : "en");
            using (var main = new MainForm(session))
            {
                Hide();
                try { main.ShowDialog(); }
                finally { Show(); }
            }
        }
    }
}
