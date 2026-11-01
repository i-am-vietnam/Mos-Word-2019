using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MosWord2019
{
    public sealed class LoginForm : Form
    {
        private readonly TextBox username = new TextBox { Name = "Username", Dock = DockStyle.Fill };
        private readonly TextBox password = new TextBox { Name = "Password", Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly RadioButton english = new RadioButton { Name = "English", Text = "Tiếng Anh", AutoSize = true, Checked = true };
        private readonly RadioButton vietnamese = new RadioButton { Name = "Vietnamese", Text = "Tiếng Việt", AutoSize = true };
        private readonly RadioButton training = new RadioButton { Name = "Training", Text = "Training", AutoSize = true, Checked = true };
        private readonly RadioButton testing = new RadioButton { Name = "Testing", Text = "Testing", AutoSize = true };
        private readonly Label heading = new Label { Name = "LoginTitle", AutoSize = true, Font = new Font("Segoe UI", 24, FontStyle.Bold) };
        private readonly Label userLabel = new Label { Name = "UsernameLabel", AutoSize = true };
        private readonly Label passwordLabel = new Label { Name = "PasswordLabel", AutoSize = true };
        private readonly Label languageLabel = new Label { AutoSize = true };
        private readonly Label functionLabel = new Label { AutoSize = true };
        private readonly Label status = new Label { Name = "LoginStatus", Dock = DockStyle.Fill, ForeColor = Color.Firebrick };
        private readonly Button enter = new Button { Name = "Continue", AutoSize = true, MinimumSize = new Size(130, 38), BackColor = Color.FromArgb(32, 100, 190), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Button exit = new Button { Name = "Exit", AutoSize = true, MinimumSize = new Size(110, 38), DialogResult = DialogResult.Cancel };
        private readonly IAuthenticationService authentication;
        private readonly LoginPreferences preferences;
        private readonly Action<AppSession> openSession;
        private string T(string en, string vi) => vietnamese.Checked ? vi : en;

        public LoginForm() : this(new OfflineAuthenticationService(), new LoginPreferences(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosWord2019", "login-preferences.txt")), null) { }

        internal LoginForm(IAuthenticationService authentication, LoginPreferences preferences, Action<AppSession> openSession)
        {
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.preferences = preferences;
            this.openSession = openSession ?? OpenSession;
            Font = new Font("Segoe UI", 10);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.White; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 450); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30, 12, 30, 12), ColumnCount = 1, RowCount = 11 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int[] heights = { 48, 22, 30, 22, 30, 22, 28, 22, 28, 44 };
            foreach (int height in heights) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var languages = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; languages.Controls.AddRange(new Control[] { english, vietnamese });
            var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; modes.Controls.AddRange(new Control[] { training, testing });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; buttons.Controls.AddRange(new Control[] { enter, exit });
            foreach (Control control in new Control[] { heading, userLabel, username, passwordLabel, password, languageLabel, languages, functionLabel, modes, buttons, status }) layout.Controls.Add(control);
            Controls.Add(layout); AcceptButton = enter; CancelButton = exit;
            english.CheckedChanged += (s, e) => Localize(); vietnamese.CheckedChanged += (s, e) => Localize();
            enter.Click += OpenShell; exit.Click += (s, e) => Close();
            preferences?.Load(); username.Text = preferences?.Username ?? "";
            if (preferences?.Language == "vi") vietnamese.Checked = true;
            Localize();
        }
        private void Localize()
        {
            Text = heading.Text = enter.Text = T("Login", "Đăng nhập");
            userLabel.Text = T("Username:", "Tài khoản:"); passwordLabel.Text = T("Password:", "Mật khẩu:");
            languageLabel.Text = T("Language:", "Ngôn ngữ:"); functionLabel.Text = T("Function:", "Chức năng:");
            exit.Text = T("Exit", "Thoát"); if (status.Text.Length > 0) ShowLoginError();
        }
        private void ShowLoginError() => status.Text = T("Please enter your username and password.", "Vui lòng nhập tài khoản và mật khẩu.");
        private void OpenShell(object sender, EventArgs e)
        {
            if (!authentication.Authenticate(username.Text, password.Text)) { ShowLoginError(); return; }
            status.Text = "";
            if (preferences != null) { preferences.Username = username.Text; preferences.Language = vietnamese.Checked ? "vi" : "en"; preferences.Save(); }
            try { openSession(new AppSession(testing.Checked ? AppMode.Testing : AppMode.Training, vietnamese.Checked ? "vi" : "en")); }
            finally { password.Clear(); }
        }
        private void OpenSession(AppSession session)
        {
            if (session.Mode == AppMode.Testing)
            {
                Hide(); try { TestingFlow.Open(this, session.Language); }
                finally { if (Program.RecoveryDeferred) Close(); else Show(); } return;
            }
            using (var main = new MainForm(session))
            {
                Hide(); try { main.ShowDialog(); }
                finally { if (Program.RecoveryDeferred) Close(); else Show(); }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown) Program.DeferRecoveryToNextStart();
            base.OnFormClosing(e);
        }
    }
}
