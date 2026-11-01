using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
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
        private readonly Func<string, bool> retryCleanup;
        private readonly ComboBox projects = new ComboBox { Name = "Projects", DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, DisplayMember = "DisplayName" };
        private readonly Button go = new Button { Name = "Go", AutoSize = true, Enabled = false };
        private readonly Button previous = new Button { Name = "Previous", AutoSize = true };
        private readonly Button next = new Button { Name = "Next", AutoSize = true };
        private readonly Button restart = new Button { Name = "Restart", AutoSize = true };
        private readonly Button grade = new Button { Name = "Grade", AutoSize = true };
        private readonly WordGradingService grading = new WordGradingService();
        private readonly TabControl tabTasks = new TabControl { Name = "tabTasks", Dock = DockStyle.Fill, Visible = false, Multiline = false };
        private readonly Label status = new Label { Name = "Status", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly Label taskCounter = new Label { Name = "TaskCounter", AutoSize = true, Anchor = AnchorStyles.Right };
        private ProjectPackage currentProject;
        private string currentWorkPath;
        private readonly TemplateOutputCleanupService templateOutputs;
        private int taskIndex;
        private bool controllerDisposed;
        private bool displayEventsSubscribed;
        private bool arranging;
        private readonly Timer savedOutputMonitor = new Timer { Interval = 250 };
        private readonly Stopwatch saveObservationWindow = new Stopwatch();
        private Exception lastSaveObservationFailure;
        private bool skipCleanup;
        private bool cleanupCompleted;
        private bool deferredCloseQueued;
        private bool deferredCloseAttempted;

        public MainForm(AppSession session) : this(session,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects"),
            Program.Workspace ?? new TrainingWorkspaceService(Program.Journal), new WordController()) { }

        // The form owns the controller. Injection keeps verification packages out of production.
        public MainForm(AppSession session, string projectRoot, TrainingWorkspaceService workspace,
            IWordController word, Func<string, bool> confirmRestart = null, Action<string> notify = null,
            TemplateOutputCleanupService outputCleanup = null, Func<string, bool> retryCleanup = null)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            if (session.Mode != AppMode.Training) throw new NotSupportedException("Testing is not implemented.");
            this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            this.word = word ?? throw new ArgumentNullException(nameof(word));
            templateOutputs = outputCleanup ?? new TemplateOutputCleanupService(workspace.DocumentsRootPath, journal: Program.Journal);
            if (word is IWordSaveEvents saveEvents)
            {
                saveEvents.OwnedDocumentSaving += OwnedDocumentSaving;
                saveEvents.WorkingFileWritten += workspace.RecordOwnedWorkingFile;
            }
            savedOutputMonitor.Tick += (sender, args) => ObserveAfterSave();
            this.retryCleanup = retryCleanup ?? ShowCleanupChoice;
            this.confirmRestart = confirmRestart ?? (message => MessageBox.Show(this, message, Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
            this.notify = notify ?? (message => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information));
            if (word is IWordExportCheckpoint checkpoint)
                checkpoint.ExportCheckpointFailed += message =>
                {
                    if (IsDisposed || Disposing || !IsHandleCreated) return;
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed || Disposing) return;
                        status.Text = T("Save cancelled: unable to preserve formatted work.", "Đã hủy lưu: không thể giữ bản làm việc có định dạng.");
                        notify(status.Text);
                    }));
                };
            Text = "MOS Word 2019";
            StartPosition = FormStartPosition.Manual;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.WhiteSmoke;
            MaximizeBox = false;
            UiLayout.ScaleForm(this);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(2), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            foreach (Control c in new Control[] { go, previous, next, restart, grade, taskCounter, status }) { c.Dock = DockStyle.None; UiLayout.Text(c); }
            var projectLabel = UiLayout.Text(new Label { Text = T("Project:", "Dự án:") });
            var selection = UiLayout.Flow(projectLabel, projects, go);
            var future = UiLayout.Text(new Label { Name = "TrainingCountdown", Visible = false, Text = "04:00:00", ForeColor = Color.Red, Font = new Font("Segoe UI", 24, FontStyle.Bold) });
            layout.Controls.Add(UiLayout.Header(selection, future, new Label { AutoSize = true, Visible = false, Margin = Padding.Empty }, "pnlTopBar", training: true), 0, 0);
            layout.Controls.Add(tabTasks, 0, 1);
            previous.Text = "← Task"; next.Text = "Task →"; go.Text = T("Go", "Bắt đầu");
            restart.Text = T("Restart Project", "Làm lại dự án"); grade.Text = T("Grade", "Kiểm tra");
            layout.Controls.Add(new TrainerActionBar("pnlFooter", UiLayout.Flow(previous, next, restart), UiLayout.Flow(grade, taskCounter), new Panel { AutoSize = true, Size = Size.Empty, Visible = false, Margin = Padding.Empty }), 0, 2);
            UiLayout.TabHeaders(tabTasks);
            status.TextChanged += (s, e) =>
            {
                status.Visible = status.Text.Length > 0 && status.Text != taskCounter.Text;
                if (IsHandleCreated && Visible) BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) ArrangeWorkspace(); }));
            };
            status.AutoEllipsis = false; UiLayout.Wrap(status, layout); layout.Controls.Add(status, 0, 3);
            Controls.Add(layout);
            Shown += (s, e) => ArrangeWorkspace();
            go.Click += (s, e) => Run(OpenSelectedProject);
            previous.Click += (s, e) => Run(() => ShowTask(taskIndex - 1));
            next.Click += (s, e) => Run(() => ShowTask(taskIndex + 1));
            restart.Click += (s, e) => Run(RestartProject);
            grade.Click += (s, e) => Run(GradeCurrentTask);
            tabTasks.SelectedIndexChanged += (s, e) => UpdateTaskNavigation();
            projects.SelectedIndexChanged += (s, e) => { go.Enabled = projects.SelectedItem != null; };
            ClearProject();
            Run(() =>
            {
                var loader = new ProjectLoader();
                var available = loader.LoadAll(projectRoot, session.Language)
                    .Where(p => string.Equals(Path.GetExtension(p.Meta.Starter), ".docx", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetExtension(p.Meta.Starter), ".doc", StringComparison.OrdinalIgnoreCase)).ToArray();
                projects.Items.AddRange(available);
                if (available.Length > 0) projects.SelectedIndex = 0;
                go.Enabled = available.Length > 0;
                status.Text = available.Length == 0 ? T("No available projects", "Chưa có dự án khả dụng") : T("Ready", "Sẵn sàng");
                if (loader.LastRejectedCount > 0)
                    status.Text += " — " + loader.LastRejectedCount + T(" project(s) could not be loaded", " dự án không thể tải");
            });
        }

        private string T(string en, string vi) { return session.Language == "vi" ? vi : en; }

        private void OpenSelectedProject()
        {
            var selected = projects.SelectedItem as ProjectPackage;
            if (selected == null) return;
            ValidateTranslations(selected);
            AppLogger.Write("Training project selected " + selected.Meta.ProjectId);
            EndTrainingSession();
            status.Text = T("Opening project...", "Đang mở dự án...");
            try
            {
                foreach (string stagedAsset in workspace.StageProjectAssetsToDocuments(selected))
                    AppLogger.Write("Training project asset available " + stagedAsset);
                string path = workspace.PrepareFreshWorkingCopy(selected);
                AppLogger.Write("Training working copy prepared " + path);
                templateOutputs.Register(selected); // Baseline before the owned document opens, never inherited from a previous Go.
                word.OpenDocument(path);
                AppLogger.Write("Training Word open " + path);
                currentProject = selected;
                currentWorkPath = path;
            }
            catch (Exception openError)
            {
                try
                {
                    if (!word.IsOpened)
                    {
                        templateOutputs.Cleanup(true);
                        workspace.CleanupProjectAssets(selected, assetLog => AppLogger.Write(assetLog));
                    }
                }
                catch (Exception observationError) { AppLogger.Write("Open failed; uncertain document state preserves ownership journal", observationError); }
                AppLogger.Write("Training project open failed", openError);
                throw;
            }
            ConfigureExportCheckpoint();
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
                    throw new MissingTranslationException("Required task translation is missing: " + task.TaskId);
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
                var page = new TabPage("Task " + (tabTasks.TabCount + 1)) { BackColor = Color.White };
                page.Controls.Add(new RichTextBox { Name = "Instruction", ReadOnly = true, BorderStyle = BorderStyle.None,
                    Dock = DockStyle.Fill, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
                    BackColor = Color.White, Font = new Font("Segoe UI", 20F), Text = currentProject.Lang[task.InstructionKey] });
                tabTasks.TabPages.Add(page);
            }
            tabTasks.Visible = true;
            restart.Enabled = true;
            grade.Enabled = true;
        }

        private void UpdateTaskNavigation()
        {
            taskIndex = tabTasks.SelectedIndex;
            bool active = currentProject != null && taskIndex >= 0;
            previous.Enabled = active && taskIndex > 0;
            next.Enabled = active && taskIndex < tabTasks.TabPages.Count - 1;
            taskCounter.Text = active ? "Task " + (taskIndex + 1) + "/" + tabTasks.TabPages.Count : "";
            if (active) status.Text = taskCounter.Text;
            if (active) status.ForeColor = SystemColors.ControlText;
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
            if (skipCleanup || arranging || IsDisposed || WindowState == FormWindowState.Minimized) return;
            arranging = true;
            try
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                int height = UiLayout.PanelHeight(this, area);
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
            if (skipCleanup || currentProject == null) return false;
            try { if (word.IsOpened) return true; }
            catch (Exception ex)
            {
                AppLogger.Write("Training document availability failed", ex);
                if (!WordBusyException.IsBusy(ex)) throw;
                ShowWordBusy();
                return false;
            }
            AppLogger.Write("Training manual Word closure " + currentWorkPath);
            EndTrainingSession();
            word.Close();
            string message = T("Word was closed. This Training session ended. Choose a project and click Go to start again from its original starter.",
                "Word đã đóng. Phiên luyện tập đã kết thúc. Chọn dự án và bấm Bắt đầu để làm lại từ tài liệu gốc.");
            status.Text = message;
            notify(message);
            return false;
        }

        private void ObserveOwnedOutput()
        {
            if (skipCleanup || templateOutputs.CurrentLedger == null) return;
            if (word.IsOpened) templateOutputs.ObserveOwnedDocument((word as IWordDocumentState)?.CurrentDocumentPath);
        }

        private void OwnedDocumentSaving(string ownedPath)
        {
            // The callback already verified COM identity. Do not query the controller from that callback.
            if (skipCleanup || controllerDisposed) return;
            try
            {
                templateOutputs.ObserveOwnedDocument(ownedPath);
                workspace.ObserveWorkingFiles(ownedPath);
            }
            catch (Exception ex) { AppLogger.Write("Owned save notification observation failed; preserve journal", ex); }
            if (skipCleanup || IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() =>
            {
                if (skipCleanup || IsDisposed || Disposing || controllerDisposed) return;
                lastSaveObservationFailure = null;
                saveObservationWindow.Restart();
                savedOutputMonitor.Interval = 250;
                savedOutputMonitor.Start();
                ObserveAfterSave();
            })); }
            catch (InvalidOperationException) { /* Closing raced the save notification. */ }
        }

        private void ObserveAfterSave()
        {
            if (skipCleanup || controllerDisposed || IsDisposed || Disposing) { savedOutputMonitor.Stop(); return; }
            if (saveObservationWindow.ElapsedMilliseconds >= 5000) { StopSaveObservation(); return; }
            try
            {
                using ((word as WordController)?.BeginShortCallBudget((int)Math.Max(1, Math.Min(250, 5000 - saveObservationWindow.ElapsedMilliseconds))))
                {
                    ObserveOwnedOutput();
                    if (word.IsOpened) workspace.ObserveWorkingFiles((word as IWordDocumentState)?.CurrentDocumentPath);
                }
                lastSaveObservationFailure = null;
            }
            catch (Exception ex) { lastSaveObservationFailure = ex; }
            int remaining = (int)(5000 - saveObservationWindow.ElapsedMilliseconds);
            if (remaining <= 0) StopSaveObservation();
            else savedOutputMonitor.Interval = Math.Min(remaining, Math.Min(1000, savedOutputMonitor.Interval * 2));
        }

        private void StopSaveObservation()
        {
            savedOutputMonitor.Stop();
            saveObservationWindow.Stop();
            if (lastSaveObservationFailure != null)
            {
                AppLogger.Write("Post-save observation deferred; ownership journal retained. " + lastSaveObservationFailure.GetType().Name + ": " + lastSaveObservationFailure.Message);
                lastSaveObservationFailure = null;
            }
        }

        private void CloseOwnedOutputDocument()
        {
            savedOutputMonitor.Stop();
            ObserveOwnedOutput();
            word.CloseDocument();
            bool closed = !word.IsOpened;
            if (closed) workspace.ObserveWorkingFiles();
            templateOutputs.Cleanup(closed); // Memo.txt may have been the live owned document until this close.
            if (!closed) throw new InvalidOperationException("The owned Word document is still open; output cleanup and reset were deferred.");
        }

        private void EndTrainingSession()
        {
            if (currentProject == null) return;
            CloseOwnedOutputDocument(); // Ownership-safe discard; never save merely for leaving Training.
            AppLogger.Write("Training document discarded " + currentWorkPath);
            // Retain project/path/assets until reset succeeds, so a failed reset is retryable.
            workspace.ResetWorkingCopy(currentProject);
            workspace.CleanupProjectAssets(currentProject, assetLog => AppLogger.Write(assetLog));
            ClearProject();
        }

        private void RestartProject()
        {
            if (!CheckDocument()) return;
            if (!confirmRestart(T("Restart this project?\nYour current changes will be discarded.",
                "Làm lại dự án này?\nCác thay đổi hiện tại sẽ bị xóa."))) return;
            var project = currentProject;
            CloseOwnedOutputDocument(); // Explicit confirmed discard; do not save.
            // Keep validated project state/assets if reset or reopen fails; the next action can retry safely.
            foreach (string stagedAsset in workspace.StageProjectAssetsToDocuments(project))
                AppLogger.Write("Training project asset available after restart " + stagedAsset);
            string path = workspace.ResetWorkingCopy(project);
            templateOutputs.Register(project); // Restart is a new output baseline, captured before Word opens.
            word.OpenDocument(path);
            currentWorkPath = path;
            ConfigureExportCheckpoint();
            BuildTaskTabs();
            ShowTask(0);
            AppLogger.Write("Training restart " + path);
            status.Text = T("Project restarted", "Đã làm lại dự án");
            ArrangeWorkspace();
        }

        private void GradeCurrentTask()
        {
            if (currentProject == null || taskIndex < 0 || taskIndex >= currentProject.Tasks.Count) return;
            TaskDefinition task = currentProject.Tasks[taskIndex];
            bool externalOutput = ExternalOutputLocation.IsExistenceAssertion(task.AssertionType);
            if (!CheckDocument()) return;
            string savedPath;
            TrainingGradeContext context;
            try
            {
                var documentState = word as IWordDocumentState;
                context = new TrainingGradeContext
                {
                    OwnedDocumentPath = documentState?.CurrentDocumentPath,
                    OwnedDocumentSaveFormat = (word as IWordSaveFormat)?.CurrentSaveFormat
                };
                bool plainText = ExternalOutputLocation.IsOwnedPlainTextOutput(context);
                // File-existence tasks inspect only their declared output; grading does not save/alter that template.
                if (!externalOutput && !plainText)
                {
                    word.Save();
                    PreserveSaveAsWork();
                }
                savedPath = documentState?.CurrentDocumentPath ?? currentWorkPath;
                if (plainText || ExternalOutputLocation.IsDeclaredDocumentsOutput(currentProject, savedPath))
                    savedPath = currentWorkPath; // Grade the retained Word working copy, never a lossy external output.
                if (!externalOutput && TrainingWorkspaceService.RequiresExportCheckpoint(currentProject))
                    savedPath = ((IWordExportCheckpoint)word).SaveFormattedCheckpoint();
                context.DefaultTemplateFolder = task.AssertionType == "SavedWordTemplate" ? documentState?.DefaultTemplateFolder : null;
                context.SavedFromCurrentWorkingDocument = !string.Equals(savedPath, currentWorkPath, StringComparison.OrdinalIgnoreCase);
                context.OutputOwnership = templateOutputs.Capture(documentState?.CurrentDocumentPath);
                workspace.ObserveWorkingFiles(documentState?.CurrentDocumentPath);
                AppLogger.Write("Training grade source " + savedPath + " task " + task.TaskId);
            }
            catch (Exception ex)
            {
                AppLogger.Write("Training grade save failed " + currentWorkPath + " task " + task.TaskId, ex);
                if (WordBusyException.IsBusy(ex)) { ShowWordBusy(); return; }
                status.ForeColor = Color.Crimson;
                status.Text = T("Grade error: unable to save the working document.", "Lỗi kiểm tra: không thể lưu tài liệu đang làm.");
                notify(T("A technical error prevented grading because the document could not be saved. Keep Word open and recover your work there.",
                    "Lỗi kỹ thuật ngăn việc kiểm tra vì không thể lưu tài liệu. Hãy giữ Word mở và khôi phục bài làm trong Word."));
                return;
            }

            if (task.AssertionType == WordFormattingMarksSnapshot.AssertionType)
            {
                try { context.FormattingMarks = (word as IWordViewState)?.ReadFormattingMarks(); }
                catch (Exception ex) { AppLogger.Write("Training grade view snapshot unavailable task " + task.TaskId, ex); }
            }
            if (WordGradingService.RequiresTocLayout(task))
            {
                try { context.TocLayout = (word as IWordTocLayout)?.ReadTocLayout(); }
                catch (Exception ex) { AppLogger.Write("Training grade TOC layout snapshot unavailable task " + task.TaskId, ex); }
            }
            TaskGradeResult result = grading.CheckTask(savedPath, task, context);
            AppLogger.Write("Training grade " + task.TaskId + " " + result.Outcome + " " + result.Message);
            if (result.Outcome == TaskGradeOutcome.Pass)
            {
                status.ForeColor = Color.DarkGreen;
                status.Text = "PASS - " + task.TaskId;
                notify(T("Correct", "Đúng"));
            }
            else if (result.Outcome == TaskGradeOutcome.Fail)
            {
                status.ForeColor = Color.Crimson;
                status.Text = "FAIL - " + task.TaskId;
                notify(T("Incorrect", "Sai"));
            }
            else
            {
                status.ForeColor = Color.Crimson;
                status.Text = T("Grade error: ", "Lỗi kiểm tra: ") + result.Message;
                notify(T("A technical error prevented grading. ", "Lỗi kỹ thuật ngăn việc kiểm tra. ") + result.Message);
            }
        }

        private void ClearProject()
        {
            currentProject = null;
            taskCounter.Text = "";
            currentWorkPath = null;
            taskIndex = 0;
            ClearTabs();
            tabTasks.Visible = previous.Enabled = next.Enabled = restart.Enabled = grade.Enabled = false;
            status.ForeColor = SystemColors.ControlText;
        }

        private void PreserveSaveAsWork()
        {
            if (TrainingWorkspaceService.RequiresExportCheckpoint(currentProject))
            {
                ((IWordExportCheckpoint)word).SaveFormattedCheckpoint();
                return;
            }
            var state = word as IWordDocumentState;
            if (state != null && currentProject != null &&
                !ExternalOutputLocation.IsDeclaredDocumentsOutput(currentProject, state.CurrentDocumentPath))
                workspace.PreserveSavedWorkingCopy(currentProject, state.CurrentDocumentPath);
        }

        private void ConfigureExportCheckpoint()
        {
            if (!TrainingWorkspaceService.RequiresExportCheckpoint(currentProject)) return;
            var checkpoint = word as IWordExportCheckpoint;
            if (checkpoint == null) throw new NotSupportedException("The Word controller cannot safely preserve a lossy export.");
            checkpoint.ConfigureExportCheckpoint(workspace.GetExportCheckpointPath(currentProject));
        }

        private void Run(Action action)
        {
            if (skipCleanup) return;
            try { action(); }
            catch (Exception ex)
            {
                AppLogger.Write("Training operation failed", ex);
                var assetConflict = ex as ProjectAssetConflictException;
                if (assetConflict != null)
                {
                    status.Text = string.Format(T(
                        "A different file named {0} already exists in {1}. Move or rename it, then try again.",
                        "Đã có tệp khác tên {0} tại {1}. Hãy di chuyển hoặc đổi tên tệp đó rồi thử lại."),
                        assetConflict.AssetFileName, assetConflict.Location);
                }
                else if (WordBusyException.IsBusy(ex))
                {
                    ShowWordBusy();
                    return;
                }
                else if (ex is MissingTranslationException)
                {
                    status.Text = T("Required task translations are missing. Correct the project language file before continuing.",
                        "Thiếu bản dịch tác vụ bắt buộc. Hãy sửa tệp ngôn ngữ của dự án trước khi tiếp tục.");
                }
                else
                {
                    status.Text = T("Unable to complete the operation. Check Word and document access, then retry. If saving failed, keep Word open and recover your changes there.",
                        "Không thể hoàn tất thao tác. Kiểm tra Word và quyền truy cập rồi thử lại. Nếu lưu thất bại, hãy giữ Word mở để khôi phục nội dung.");
                }
                notify(status.Text);
            }
        }

        private void ShowWordBusy()
        {
            status.ForeColor = Color.Crimson;
            status.Text = T("Please close any open dialog in Word and try again.",
                "Hãy đóng hộp thoại đang mở trong Word rồi thử lại.");
            notify(status.Text);
        }

        private void CleanupOwnedSession()
        {
            if (skipCleanup || cleanupCompleted) return;
            savedOutputMonitor.Stop();
            if (!controllerDisposed)
            {
                EndTrainingSession();
                ObserveOwnedOutput();
                word.Dispose();
                controllerDisposed = true;
            }
            templateOutputs.Cleanup(true);
            workspace.CleanupWorkingFiles();
            cleanupCompleted = true;
            AppLogger.Write("Training session closed");
        }

        private void DeferRecovery(string reason, Exception error = null)
        {
            skipCleanup = true;
            try { savedOutputMonitor.Stop(); }
            catch (Exception ex) { AppLogger.Write("Unable to stop save monitor during deferred recovery", ex); }
            Program.DeferRecoveryToNextStart();
            AppLogger.Write(reason + "; retained journal for next-start recovery", error);
        }

        private static bool IsCalloutFailure(Exception error)
        {
            if (error == null) return false;
            var com = error as COMException;
            if (com != null && com.ErrorCode == unchecked((int)0x8001010D)) return true;
            var aggregate = error as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Any(IsCalloutFailure)) return true;
            return IsCalloutFailure(error.InnerException);
        }

        private bool TryCleanupWithChoice()
        {
            while (!skipCleanup && !cleanupCompleted)
            {
                try { CleanupOwnedSession(); }
                catch (Exception ex)
                {
                    AppLogger.Write("Training close cleanup failed", ex);
                    if (IsCalloutFailure(ex) && !deferredCloseAttempted)
                    {
                        QueueDeferredClose();
                        return skipCleanup;
                    }
                    string message = T("Word cleanup could not finish: ", "Không thể hoàn tất dọn dẹp Word: ") + ex.GetBaseException().Message +
                        T("\nRetry cleanup, or Close Anyway and recover owned files when the trainer next starts.",
                          "\nThử lại, hoặc Đóng ngay và khôi phục các tệp thuộc phiên khi mở ứng dụng lần sau.");
                    if (!retryCleanup(message)) DeferRecovery("User chose Close Anyway", ex);
                }
            }
            return true;
        }

        private bool ShowCleanupChoice(string message)
        {
            using (var dialog = new Form { Text = T("Unable to finish cleanup", "Không thể dọn dẹp"),
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(520, 190),
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var reason = new Label { Text = message, Bounds = new Rectangle(14, 14, 490, 116) };
                var retry = new Button { Text = T("Retry", "Thử lại"), DialogResult = DialogResult.Retry, Bounds = new Rectangle(224, 143, 130, 32) };
                var close = new Button { Text = T("Close Anyway", "Đóng ngay"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(364, 143, 140, 32) };
                dialog.Controls.AddRange(new Control[] { reason, retry, close });
                dialog.AcceptButton = retry;
                dialog.CancelButton = close;
                return dialog.ShowDialog(this) == DialogResult.Retry;
            }
        }

        private void QueueDeferredClose()
        {
            if (deferredCloseQueued || skipCleanup || cleanupCompleted) return;
            deferredCloseQueued = true;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    deferredCloseQueued = false;
                    deferredCloseAttempted = true;
                    if (IsDisposed || Disposing || skipCleanup) return;
                    try { if (TryCleanupWithChoice()) Close(); }
                    catch (Exception ex) { DeferRecovery("Deferred close failed", ex); Close(); }
                }));
            }
            catch (Exception ex) { DeferRecovery("Deferred close could not be queued", ex); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown)
            {
                DeferRecovery("Windows shutdown");
            }
            else if (!skipCleanup && !cleanupCompleted)
            {
                if (e.CloseReason == CloseReason.TaskManagerClosing && !deferredCloseAttempted)
                {
                    e.Cancel = true;
                    QueueDeferredClose(); // No COM while Task Manager's synchronous close notification is active.
                    if (skipCleanup) e.Cancel = false;
                }
                else e.Cancel = !TryCleanupWithChoice();
            }
            base.OnFormClosing(e);
        }

        private void DisposeSafely(Action action, string operation)
        {
            try { action(); }
            catch (Exception ex) { DeferRecovery(operation, ex); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (displayEventsSubscribed)
                {
                    DisposeSafely(() => SystemEvents.DisplaySettingsChanged -= DisplayChanged, "Unsubscribe display events failed");
                    DisposeSafely(() => SystemEvents.UserPreferenceChanged -= PreferencesChanged, "Unsubscribe preference events failed");
                    displayEventsSubscribed = false;
                }
                if (!skipCleanup) DisposeSafely(CleanupOwnedSession, "Dispose cleanup failed");
                DisposeSafely(savedOutputMonitor.Dispose, "Dispose save monitor failed");
                if (!skipCleanup && word is IWordSaveEvents saveEvents)
                {
                    DisposeSafely(() => saveEvents.OwnedDocumentSaving -= OwnedDocumentSaving, "Unsubscribe owned save failed");
                    if (!skipCleanup) DisposeSafely(() => saveEvents.WorkingFileWritten -= workspace.RecordOwnedWorkingFile, "Unsubscribe working save failed");
                }
            }
            try { base.Dispose(disposing); }
            catch (Exception ex) { DeferRecovery("Base form disposal failed", ex); }
        }

        internal void CloseOwnedForRecovery()
        {
            if (skipCleanup) return;
            try { CleanupOwnedSession(); }
            catch (Exception ex) { DeferRecovery("Fatal cleanup failed", ex); throw; }
        }
    }
}
