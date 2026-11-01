using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MosWord2019
{
    public sealed class LoginForm : Form
    {
        private readonly TextBox username = new TextBox { Name = "Username", Width = 240, Anchor = AnchorStyles.Left };
        private readonly TextBox password = new TextBox { Name = "Password", Width = 240, Anchor = AnchorStyles.Left, UseSystemPasswordChar = true };
        private readonly RadioButton english = new RadioButton { Name = "English", Text = "Tiếng Anh", AutoSize = true, Checked = true };
        private readonly RadioButton vietnamese = new RadioButton { Name = "Vietnamese", Text = "Tiếng Việt", AutoSize = true };
        private readonly RadioButton training = new RadioButton { Name = "Training", Text = "Training", AutoSize = true, Checked = true };
        private readonly RadioButton testing = new RadioButton { Name = "Testing", Text = "Testing", AutoSize = true };
        private readonly Label heading = new Label { Name = "LoginTitle", AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
        private readonly Label userLabel = new Label { Name = "UsernameLabel", AutoSize = true };
        private readonly Label passwordLabel = new Label { Name = "PasswordLabel", AutoSize = true };
        private readonly Label languageLabel = new Label { AutoSize = true };
        private readonly Label functionLabel = new Label { AutoSize = true };
        private readonly Label status = new Label { Name = "LoginStatus", AutoSize = true, Anchor = AnchorStyles.None, TextAlign = ContentAlignment.MiddleCenter, Visible = false, ForeColor = Color.Firebrick };
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
            UiLayout.ScaleForm(this, 10);
            BackColor = Color.White; StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var layout = new TableLayoutPanel { Name = "LoginLayout", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            foreach (Control c in new Control[] { heading, userLabel, passwordLabel, languageLabel, functionLabel, status, english, vietnamese, training, testing, enter, exit }) UiLayout.Text(c);
            foreach (var label in new[] { userLabel, passwordLabel, languageLabel, functionLabel }) { label.Anchor = AnchorStyles.Right; label.TextAlign = ContentAlignment.MiddleRight; label.Margin = new Padding(4, 8, 12, 8); }
            heading.Anchor = AnchorStyles.None; heading.Margin = new Padding(8, 8, 8, 20);
            layout.Controls.Add(heading, 0, 0); layout.SetColumnSpan(heading, 2);
            layout.Controls.Add(userLabel, 0, 1); layout.Controls.Add(username, 1, 1);
            layout.Controls.Add(passwordLabel, 0, 2); layout.Controls.Add(password, 1, 2);
            layout.Controls.Add(languageLabel, 0, 3); layout.Controls.Add(UiLayout.Flow(english, vietnamese), 1, 3);
            layout.Controls.Add(functionLabel, 0, 4); layout.Controls.Add(UiLayout.Flow(training, testing), 1, 4);
            var buttons = UiLayout.Flow(enter, exit); buttons.Margin = new Padding(0, 16, 0, 4);
            layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 2);
            layout.Controls.Add(status, 0, 6); layout.SetColumnSpan(status, 2);
            UiLayout.EqualButtons(enter, exit);
            status.TextChanged += (s, e) => status.Visible = status.Text.Length > 0;
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
