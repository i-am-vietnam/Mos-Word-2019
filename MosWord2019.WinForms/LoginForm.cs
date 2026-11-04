using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Threading.Tasks;
using MosWord2019.Projects;
using MosWord2019.Projects.OnlineSessions;

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
        private bool busy, authenticated;
        private string errorCode;
        private string T(string en, string vi) => vietnamese.Checked ? vi : en;

        private static IAuthenticationService DefaultAuthentication()
        {
#if DEBUG
            if (System.Configuration.ConfigurationManager.AppSettings["UseOfflineAuthentication"] == "true") return new OfflineAuthenticationService();
#endif
            return new OnlineAuthenticationService();
        }
        public LoginForm() : this(DefaultAuthentication(), new LoginPreferences(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosWord2019", "login-preferences.txt")), null) { }

        internal LoginForm(IAuthenticationService authentication, LoginPreferences preferences, Action<AppSession> openSession)
        {
            this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
            this.preferences = preferences;
            this.openSession = openSession ?? OpenSession;
            if (authentication is IOnlineAuthenticationService) { training.Enabled = false; testing.Enabled = false; }
            UiLayout.ScaleForm(this, 10);
            BackColor = Color.White; StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var layout = new TableLayoutPanel { Name = "LoginLayout", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            var version = UiLayout.Text(new Label { Name = "ProductVersion", Text = "v" +
                System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(LoginForm).Assembly).InformationalVersion,
                Anchor = AnchorStyles.None, ForeColor = Color.DimGray });
            layout.Controls.Add(version, 0, 7); layout.SetColumnSpan(version, 2);
            UiLayout.EqualButtons(enter, exit);
            status.TextChanged += (s, e) => status.Visible = status.Text.Length > 0;
            Controls.Add(layout); AcceptButton = enter; CancelButton = exit;
            english.CheckedChanged += (s, e) => Localize(); vietnamese.CheckedChanged += (s, e) => Localize();
            enter.Click += OpenShell; exit.Click += (s, e) => Close();
            preferences?.Load(); username.Text = preferences?.Username ?? "";
            if (preferences?.Language == "vi") vietnamese.Checked = true;
            Localize();
            UiLayout.Wrap(status, layout);
            if (authentication is IOnlineAuthenticationService resumed && resumed.Credentials != null)
            {
                authenticated = true; username.Enabled = password.Enabled = false;
                training.Enabled = testing.Enabled = true; ShowRemaining();
            }
        }
        private void Localize()
        {
            Text = heading.Text = enter.Text = T("Login", "Đăng nhập");
            userLabel.Text = T("Username:", "Tài khoản:"); passwordLabel.Text = T("Password:", "Mật khẩu:");
            languageLabel.Text = T("Language:", "Ngôn ngữ:"); functionLabel.Text = T("Function:", "Chức năng:");
            exit.Text = T("Exit", "Thoát");
            if (errorCode != null) ShowLoginError(errorCode);
            else if (authenticated) ShowRemaining();
        }
        private void ShowLoginError(string code = "missing_credentials")
        {
            errorCode = code; status.ForeColor = Color.Firebrick;
            switch (code)
            {
                case "no_uses_left": status.Text = T("No uses left. Please contact the administrator.", "Đã hết lượt sử dụng. Vui lòng liên hệ quản trị viên."); break;
                case "account_expired": status.Text = T("This account has expired.", "Tài khoản đã hết hạn."); break;
                case "account_locked": status.Text = T("This account is locked by the administrator.", "Tài khoản đã bị quản trị viên khóa."); break;
                case "invalid_credentials": status.Text = T("Incorrect credentials or temporarily locked. Please try again later.", "Sai tài khoản/mật khẩu hoặc tài khoản đang tạm khóa. Vui lòng thử lại sau."); break;
                case "certificate_mismatch": status.Text = T("Server certificate mismatch. Connection refused.", "Chứng chỉ server không khớp. Đã từ chối kết nối."); break;
                case "configuration_error": status.Text = T("Configure the server URL and certificate pin before logging in.", "Cần cấu hình địa chỉ server và pin chứng chỉ trước khi đăng nhập."); break;
                case "server_unreachable": status.Text = T("The server is unreachable. Please check your connection.", "Không kết nối được server. Vui lòng kiểm tra mạng."); break;
                case "retry_later": status.Text = T("Please wait and try again later.", "Vui lòng chờ và thử lại sau."); break;
                case "missing_credentials": status.Text = T("Please enter your username and password.", "Vui lòng nhập tài khoản và mật khẩu."); break;
                default: status.Text = T("The online session could not be opened. Please retry.", "Không thể mở phiên online. Vui lòng thử lại."); break;
            }
        }
        private void ShowRemaining()
        {
            var online = authentication as IOnlineAuthenticationService;
            errorCode = null; status.ForeColor = Color.DarkGreen;
            status.Text = T("Remaining uses: ", "Số lượt còn lại: ") + online.Credentials.RemainingUses +
                T(". Choose a mode and continue.", ". Chọn chế độ rồi tiếp tục.");
            enter.Text = T("Continue", "Tiếp tục");
        }
        private async void OpenShell(object sender, EventArgs e)
        {
            if (busy) return;
            var online = authentication as IOnlineAuthenticationService;
            busy = true; enter.Enabled = false; exit.Enabled = false;
            OnlineSessionContext context = null;
            try
            {
                if (online != null && !authenticated)
                {
                    if (!await online.AuthenticateAsync(username.Text, password.Text)) { ShowLoginError(online.LastErrorCode); return; }
                    authenticated = true; username.Enabled = password.Enabled = false;
                    training.Enabled = testing.Enabled = true; password.Clear(); ShowRemaining(); return;
                }
                if (online == null && !authentication.Authenticate(username.Text, password.Text)) { ShowLoginError(); return; }
                string language = vietnamese.Checked ? "vi" : "en";
                AppMode mode = testing.Checked ? AppMode.Testing : AppMode.Training;
                if (preferences != null) { preferences.Username = username.Text; preferences.Language = language; preferences.Save(); }
                if (online != null)
                {
                    training.Enabled = testing.Enabled = english.Enabled = vietnamese.Enabled = false;
                    context = await online.SelectModeAsync(mode, language);
                }
                errorCode = null; status.Text = "";
                openSession(new AppSession(mode, language, context));
                if (context != null && mode == AppMode.Testing && new TestingSessionStateStore().RequiresRecovery)
                    Program.DeferRecoveryToNextStart();
                if (context != null && !Program.RecoveryDeferred) await context.EndAsync();
                string message = context?.TerminalMessage;
                authenticated = false; username.Enabled = password.Enabled = true;
                training.Enabled = testing.Enabled = online == null;
                english.Enabled = vietnamese.Enabled = true;
                Localize(); status.Text = message ?? "";
            }
            catch (OnlineSessionException ex) { ShowLoginError(ex.ErrorCode); }
            catch (Exception ex)
            {
                MosWord2019.Core.Diagnostics.AppLogger.Write("Online opening deferred (" + ex.GetType().Name + ").");
                ShowLoginError("server_unreachable");
            }
            finally
            {
                if (context != null)
                {
                    if (Program.RecoveryDeferred) context.Monitor.Dispose();
                    else context.Dispose();
                }
                busy = false; enter.Enabled = exit.Enabled = true; password.Clear();
                if (authenticated && online?.Credentials?.Mode != null)
                { training.Checked = online.Credentials.Mode == "Training"; testing.Checked = !training.Checked; training.Enabled = testing.Enabled = false; }
                if (context != null && Program.RecoveryDeferred) Close();
            }
        }
        private void OpenSession(AppSession session)
        {
            if (session.Mode == AppMode.Testing)
            {
                Hide(); try { TestingFlow.Open(this, session.Language, session.Online); }
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
            if (busy && authentication is IOnlineAuthenticationService && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            if (authenticated && !Program.RecoveryDeferred && !busy && authentication is IOnlineAuthenticationService online)
            { e.Cancel = true; _ = CancelPendingLoginAsync(online); return; }
            base.OnFormClosing(e);
        }
        private async Task CancelPendingLoginAsync(IOnlineAuthenticationService online)
        {
            busy = true;
            try { await online.CancelAsync(); }
            catch (Exception) { /* A deliberate local end is retained by the server policy if unreachable. */ }
            finally { authenticated = false; busy = false; Close(); }
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            (authentication as IDisposable)?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
