using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
using MosWord2019.Word;
using Microsoft.Win32;

namespace MosWord2019
{
    public sealed class MainForm : Form
    {
        private readonly AppSession session;
        private readonly IWordController word;
        private readonly TrainingWorkspaceService workspace;
        private readonly Func<string, bool> confirmRestart;
        private readonly Action<string> notify;
        private readonly ComboBox projects = new ComboBox { Name = "Projects", DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, DisplayMember = "DisplayName" };
        private readonly Button go = new Button { Name = "Go", AutoSize = true, Enabled = false };
        private readonly Button previous = new Button { Name = "Previous", AutoSize = true };
        private readonly Button next = new Button { Name = "Next", AutoSize = true };
        private readonly Button restart = new Button { Name = "Restart", AutoSize = true };
        private readonly TabControl tabTasks = new TabControl { Name = "tabTasks", Dock = DockStyle.Fill, Visible = false, Multiline = true };
        private readonly Label status = new Label { Name = "Status", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private ProjectPackage currentProject;
        private string currentWorkPath;
        private int taskIndex;
        private bool controllerDisposed;
        private bool displayEventsSubscribed;
        private bool arranging;

        public MainForm(AppSession session) : this(session,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects"),
            new TrainingWorkspaceService(), new WordController()) { }

        // The form owns the controller. Injection keeps verification packages out of production.
        public MainForm(AppSession session, string projectRoot, TrainingWorkspaceService workspace,
            IWordController word, Func<string, bool> confirmRestart = null, Action<string> notify = null)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            if (session.Mode != AppMode.Training) throw new NotSupportedException("Testing is not implemented.");
            this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            this.word = word ?? throw new ArgumentNullException(nameof(word));
            this.confirmRestart = confirmRestart ?? (message => MessageBox.Show(this, message, Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
            this.notify = notify ?? (message => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information));
            Text = "MOS Word 2019";
            StartPosition = FormStartPosition.Manual;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.WhiteSmoke;
            MaximizeBox = false;
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(4), ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var selection = new FlowLayoutPanel { Name = "pnlTopBar", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(6), WrapContents = false };
            selection.Controls.Add(new Label { Text = T("Project:", "Dự án:"), AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            selection.Controls.Add(projects);
            go.Text = T("Go", "Bắt đầu");
            selection.Controls.Add(go);
            layout.Controls.Add(selection, 0, 0);
            layout.Controls.Add(tabTasks, 0, 1);
            var actions = new TableLayoutPanel { Name = "pnlFooter", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(6), RowCount = 1, ColumnCount = 4 };
            for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            previous.Text = "<<";
            next.Text = ">>";
            previous.MinimumSize = next.MinimumSize = new Size(50, 28);
            go.MinimumSize = new Size(60, 28);
            restart.Text = T("Restart Project", "Làm lại dự án");
            restart.MinimumSize = new Size(120, 28);
            actions.Controls.Add(previous, 0, 0);
            actions.Controls.Add(next, 1, 0);
            actions.Controls.Add(restart, 2, 0);
            actions.Controls.Add(status, 3, 0);
            layout.Controls.Add(actions, 0, 2);
            Controls.Add(layout);
            go.Click += (s, e) => Run(OpenSelectedProject);
            previous.Click += (s, e) => Run(() => ShowTask(taskIndex - 1));
            next.Click += (s, e) => Run(() => ShowTask(taskIndex + 1));
            restart.Click += (s, e) => Run(RestartProject);
            tabTasks.SelectedIndexChanged += (s, e) => UpdateTaskNavigation();
            projects.SelectedIndexChanged += (s, e) => { go.Enabled = projects.SelectedItem != null; };
            ClearProject();
            Run(() =>
            {
                var available = new ProjectLoader().LoadAll(projectRoot, session.Language)
                    .Where(p => string.Equals(Path.GetExtension(p.Meta.Starter), ".docx", StringComparison.OrdinalIgnoreCase)).ToArray();
                projects.Items.AddRange(available);
                if (available.Length > 0) projects.SelectedIndex = 0;
                go.Enabled = available.Length > 0;
                status.Text = available.Length == 0 ? T("No available projects", "Chưa có dự án khả dụng") : T("Ready", "Sẵn sàng");
            });
        }

        private string T(string en, string vi) { return session.Language == "vi" ? vi : en; }

        private void OpenSelectedProject()
        {
            var selected = projects.SelectedItem as ProjectPackage;
            if (selected == null) return;
            ValidateTranslations(selected);
            AppLogger.Write("Training project selected " + selected.Meta.ProjectId);
            SaveAndCloseDocument();
            status.Text = T("Opening project...", "Đang mở dự án...");
            string path = workspace.PrepareWorkingCopy(selected);
            AppLogger.Write("Training working copy prepared " + path);
            word.OpenDocument(path);
            AppLogger.Write("Training Word open " + path);
            currentProject = selected;
            currentWorkPath = path;
            BuildTaskTabs();
            ShowTask(0);
            status.Text = T("Project opened", "Đã mở dự án");
            ArrangeWorkspace();
        }

        private static void ValidateTranslations(ProjectPackage package)
        {
            foreach (var task in package.Tasks)
            {
                string text;
                if (!package.Lang.TryGetValue(task.TitleKey, out text) || string.IsNullOrWhiteSpace(text) ||
                    !package.Lang.TryGetValue(task.InstructionKey, out text) || string.IsNullOrWhiteSpace(text))
                    throw new InvalidDataException("Required task translation is missing: " + task.TaskId);
            }
        }

        private void ShowTask(int index)
        {
            if (currentProject == null || index < 0 || index >= currentProject.Tasks.Count) return;
            ValidateTranslations(currentProject);
            tabTasks.SelectedIndex = index;
            UpdateTaskNavigation();
        }

        private void BuildTaskTabs()
        {
            ClearTabs();
            foreach (var task in currentProject.Tasks)
            {
                var page = new TabPage(currentProject.Lang[task.TitleKey]) { AutoScroll = true, BackColor = Color.White };
                var instruction = new Label { AutoSize = true, Location = new Point(0, 0), Padding = new Padding(10),
                    Font = new Font("Segoe UI", 10F), Text = currentProject.Lang[task.InstructionKey] };
                page.Controls.Add(instruction);
                // Wrapping plus scrolling preserves long instructions on small/high-DPI displays.
                page.Resize += (s, e) => instruction.MaximumSize = new Size(Math.Max(1, page.ClientSize.Width - SystemInformation.VerticalScrollBarWidth), 0);
                tabTasks.TabPages.Add(page);
            }
            tabTasks.Visible = true;
            restart.Enabled = true;
        }

        private void UpdateTaskNavigation()
        {
            taskIndex = tabTasks.SelectedIndex;
            bool active = currentProject != null && taskIndex >= 0;
            previous.Enabled = active && taskIndex > 0;
            next.Enabled = active && taskIndex < tabTasks.TabPages.Count - 1;
            if (active) status.Text = T("Task ", "Nhiệm vụ ") + (taskIndex + 1) + "/" + tabTasks.TabPages.Count;
        }

        private void ClearTabs()
        {
            while (tabTasks.TabPages.Count > 0)
            {
                TabPage page = tabTasks.TabPages[0];
                tabTasks.TabPages.Remove(page);
                page.Dispose();
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ArrangeWorkspace();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.UserPreferenceChanged += PreferencesChanged;
            displayEventsSubscribed = true;
        }

        private void DisplayChanged(object sender, EventArgs e) { QueueArrangement(); }
        private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e) { QueueArrangement(); }
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            QueueArrangement();
        }
        private void QueueArrangement()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) ArrangeWorkspace(); })); }
            catch (InvalidOperationException) { /* Form closed while a system notification was queued. */ }
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            ArrangeWorkspace();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x02E0) QueueArrangement(); // WM_DPICHANGED
        }

        private void ArrangeWorkspace()
        {
            if (arranging || IsDisposed || WindowState == FormWindowState.Minimized) return;
            arranging = true;
            try
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                int minimum = (int)Math.Ceiling(Font.Height * 12.5);
                int height = Math.Min(area.Height, Math.Max(area.Height / 4, minimum));
                Bounds = new Rectangle(area.Left, area.Bottom - height, area.Width, height);
                if (currentProject != null && word is IWordWindowLayout placement && word.IsOpened)
                    placement.SetWindowBounds(area.Left, area.Top, area.Width, area.Height - height);
            }
            catch (Exception ex)
            {
                AppLogger.Write("Training window placement failed", ex);
                status.Text = T("Unable to arrange Word. You can move its window manually.", "Không thể sắp xếp Word. Bạn có thể tự di chuyển cửa sổ.");
            }
            finally { arranging = false; }
        }

        private bool CheckDocument()
        {
            if (currentProject == null) return false;
            if (word.IsOpened) return true;
            AppLogger.Write("Training manual Word closure " + currentWorkPath);
            word.Close();
            ClearProject();
            string message = T("Word was closed. Choose a project and click Go to reopen the saved working copy.",
                "Word đã đóng. Chọn dự án và bấm Bắt đầu để mở lại bản làm việc đã lưu.");
            status.Text = message;
            notify(message);
            return false;
        }

        private void SaveAndCloseDocument()
        {
            if (currentProject == null) return;
            if (!CheckDocument()) return;
            word.Save(); // Failure cancels switching/exit and preserves live learner work.
            AppLogger.Write("Training save " + currentWorkPath);
            word.CloseDocument();
            AppLogger.Write("Training document close " + currentWorkPath);
            ClearProject();
        }

        private void RestartProject()
        {
            if (!CheckDocument()) return;
            if (!confirmRestart(T("Restart this project?\nYour current changes will be discarded.",
                "Làm lại dự án này?\nCác thay đổi hiện tại sẽ bị xóa."))) return;
            var project = currentProject;
            word.CloseDocument(); // Explicit confirmed discard; do not save.
            ClearProject();
            string path = workspace.ResetWorkingCopy(project);
            word.OpenDocument(path);
            currentProject = project;
            currentWorkPath = path;
            BuildTaskTabs();
            ShowTask(0);
            AppLogger.Write("Training restart " + path);
            status.Text = T("Project restarted", "Đã làm lại dự án");
            ArrangeWorkspace();
        }

        private void ClearProject()
        {
            currentProject = null;
            currentWorkPath = null;
            taskIndex = 0;
            ClearTabs();
            tabTasks.Visible = previous.Enabled = next.Enabled = restart.Enabled = false;
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                AppLogger.Write("Training operation failed", ex);
                status.Text = ex is InvalidDataException
                    ? T("Required task translations are missing. Correct the project language file before continuing.",
                        "Thiếu bản dịch tác vụ bắt buộc. Hãy sửa tệp ngôn ngữ của dự án trước khi tiếp tục.")
                    : T("Unable to complete the operation. Check Word and document access, then retry. If saving failed, keep Word open and recover your changes there.",
                    "Không thể hoàn tất thao tác. Kiểm tra Word và quyền truy cập rồi thử lại. Nếu lưu thất bại, hãy giữ Word mở để khôi phục nội dung.");
                notify(status.Text);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool completed = false;
            Run(() =>
            {
                SaveAndCloseDocument();
                word.Dispose();
                controllerDisposed = true;
                AppLogger.Write("Training session closed");
                completed = true;
            });
            e.Cancel = !completed;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && displayEventsSubscribed)
            {
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                SystemEvents.UserPreferenceChanged -= PreferencesChanged;
                displayEventsSubscribed = false;
            }
            if (disposing && !controllerDisposed)
            {
                SaveAndCloseDocument();
                word.Dispose();
                controllerDisposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
