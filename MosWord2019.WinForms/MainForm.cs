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
        private readonly Button save = new Button { Name = "Save", AutoSize = true };
        private readonly Label info = new Label { Name = "ProjectInfo", AutoSize = true };
        private readonly Label title = new Label { Name = "TaskTitle", AutoSize = true };
        private readonly TextBox instructions = new TextBox { Name = "Instructions", Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        private readonly Label status = new Label { Name = "Status", AutoSize = true };
        private ProjectPackage currentProject;
        private string currentWorkPath;
        private int taskIndex;
        private bool controllerDisposed;

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
            Text = "MOS Word 2019 — " + T("Training", "Luyện tập");
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 480);
            MinimumSize = new Size(640, 400);
            AutoScaleMode = AutoScaleMode.Font;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 100 : 0));
            var selection = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            selection.Controls.Add(new Label { Text = T("Project", "Dự án"), AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            selection.Controls.Add(projects);
            go.Text = T("Go", "Bắt đầu");
            selection.Controls.Add(go);
            layout.Controls.Add(selection, 0, 0);
            layout.Controls.Add(info, 0, 1);
            layout.Controls.Add(title, 0, 2);
            layout.Controls.Add(instructions, 0, 3);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            previous.Text = T("Previous Task", "Tác vụ trước");
            next.Text = T("Next Task", "Tác vụ tiếp");
            restart.Text = T("Restart Project", "Làm lại dự án");
            save.Text = T("Save", "Lưu");
            var close = new Button { Name = "Close", Text = T("Save / Close", "Lưu / Đóng"), AutoSize = true };
            actions.Controls.AddRange(new Control[] { previous, next, restart, save, close });
            layout.Controls.Add(actions, 0, 4);
            layout.Controls.Add(status, 0, 5);
            Controls.Add(layout);
            go.Click += (s, e) => Run(OpenSelectedProject);
            previous.Click += (s, e) => Run(() => ShowTask(taskIndex - 1));
            next.Click += (s, e) => Run(() => ShowTask(taskIndex + 1));
            restart.Click += (s, e) => Run(RestartProject);
            save.Click += (s, e) => Run(() => { if (CheckDocument()) { word.Save(); AppLogger.Write("Training save " + currentWorkPath); status.Text = T("Saved", "Đã lưu"); } });
            close.Click += (s, e) => Close();
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
            ShowTask(0);
            status.Text = T("Project opened", "Đã mở dự án");
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
            taskIndex = index;
            var task = currentProject.Tasks[index];
            info.Text = currentProject.DisplayName + " — " + T("Task ", "Tác vụ ") + (index + 1) + " / " + currentProject.Tasks.Count;
            title.Text = currentProject.Lang[task.TitleKey];
            instructions.Text = currentProject.Lang[task.InstructionKey];
            instructions.Enabled = restart.Enabled = save.Enabled = true;
            previous.Enabled = index > 0;
            next.Enabled = index < currentProject.Tasks.Count - 1;
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
            ShowTask(0);
            AppLogger.Write("Training restart " + path);
            status.Text = T("Project restarted", "Đã làm lại dự án");
        }

        private void ClearProject()
        {
            currentProject = null;
            currentWorkPath = null;
            taskIndex = 0;
            info.Text = title.Text = instructions.Text = "";
            instructions.Enabled = previous.Enabled = next.Enabled = restart.Enabled = save.Enabled = false;
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
