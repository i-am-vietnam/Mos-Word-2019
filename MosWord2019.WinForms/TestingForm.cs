using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
namespace MosWord2019
{
    public sealed class TestingForm : Form
    {
        private readonly PreparedTestingSession prepared;
        private readonly IReadOnlyList<ProjectPackage> packages;
        private readonly TestingStaHost host;
        private readonly bool resume;
        private readonly Button start = new Button { Name = "Start", AutoSize = true };
        private readonly Button next = new Button { Name = "NextProject", AutoSize = true };
        private readonly Button restart = new Button { Name = "RestartProject", AutoSize = true };
        private readonly Button submit = new Button { Name = "Submit", AutoSize = true };
        private readonly Button recover = new Button { Name = "Recover", AutoSize = true, Visible = false };
        private readonly Button offlineSubmit = new Button { Name = "OfflineSubmit", AutoSize = true };
        private readonly Label countdown = new Label { Name = "Countdown", AutoSize = true, Anchor = AnchorStyles.None, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Red, Font = new Font("Segoe UI", 24, FontStyle.Bold) };
        private readonly Label project = new Label { Name = "ProjectIndex", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
        private readonly Label projectId = new Label { Name = "ProjectId", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly Label taskCounter = new Label { Name = "TaskCounter", AutoSize = true };
        private readonly Button previousTask = new Button { Name = "PreviousTask", Text = "← Task", AutoSize = true };
        private readonly Button nextTask = new Button { Name = "NextTask", Text = "Task →", AutoSize = true };
        private readonly CheckBox review = new CheckBox { AutoSize = true, Font = new Font("Segoe UI", 8) };
        private bool updatingReview;
        private readonly Label status = new Label { Name = "ExamStatus", AutoSize = true };
        private readonly TabControl tabs = new TabControl { Name = "ExamTasks", Dock = DockStyle.Fill, Multiline = true };
        private readonly RichTextBox rules = new RichTextBox { Name = "ExamRules", ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        private readonly Panel offline = new Panel { Name = "OfflineOverlay", Dock = DockStyle.Fill, BackColor = Color.LemonChiffon };
        private readonly Dictionary<string, bool> marks = new Dictionary<string, bool>();
        private TestingSessionSnapshot snapshot;
        private bool renderedOperation, renderedRecovery;
        private string shownProject;
        private bool operation, allowClose, skipDisposal, arranging, resultShown, closeQueued;
        internal Func<string, bool> ConfirmChoice { get; set; }
        internal Action<ExamResult> DisplayResult { get; set; }
        private GradingProgress progress;
        internal bool RecoveryRequired { get; private set; }
        private string T(string en, string vi) => prepared.Language == "vi" ? vi : en;
        public TestingForm(PreparedTestingSession prepared, IReadOnlyList<ProjectPackage> packages, TestingStaHost host = null, bool resume = false)
        {
            this.prepared = prepared; this.packages = packages; this.host = host; this.resume = resume;
            Text = "MOS Word 2019 — Testing"; StartPosition = FormStartPosition.Manual; MinimumSize = new Size(640, 100);
            UiLayout.ScaleForm(this); DoubleBuffered = true; BackColor = Color.White;
            var layout = new BufferedTableLayoutPanel { Name = "ExamLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(2) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            foreach (Control c in new Control[] { projectId, project, taskCounter, review, status, start, previousTask, nextTask, restart, next, recover, submit, offlineSubmit }) { c.Dock = DockStyle.None; UiLayout.Text(c); }
            UiLayout.Clock(countdown);
            projectId.AutoEllipsis = false;
            layout.Controls.Add(UiLayout.Header(projectId, countdown, project, "ExamTopBar"), 0, 0);
            var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; body.Controls.Add(tabs); body.Controls.Add(rules); body.Controls.Add(offline); layout.Controls.Add(body, 0, 1);
            var left = UiLayout.Flow(start, previousTask, nextTask, restart, recover);
            var center = UiLayout.Flow(next, taskCounter, review);
            submit.BackColor = Color.FromArgb(30, 125, 65); submit.ForeColor = Color.White;
            submit.FlatStyle = FlatStyle.Flat; submit.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            submit.MinimumSize = new Size(160, submit.MinimumSize.Height);
            layout.Controls.Add(new TrainerActionBar("ExamFooter", left, center, submit), 0, 2);
            UiLayout.TabHeaders(tabs);
            status.Visible = false; status.TextChanged += (s, e) =>
            {
                status.Visible = status.Text.Length > 0;
                if (IsHandleCreated && Visible) BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) Arrange(); }));
            };
            UiLayout.Wrap(status, layout); layout.Controls.Add(status, 0, 3); Controls.Add(layout);
            rules.Font = new Font("Segoe UI", 18); rules.BackColor = Color.White;
            start.Font = new Font("Segoe UI", 12, FontStyle.Bold); start.BackColor = Color.FromArgb(32, 100, 190); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat;
            previousTask.Click += (s, e) => { if (tabs.SelectedIndex > 0) tabs.SelectedIndex--; };
            nextTask.Click += (s, e) => { if (tabs.SelectedIndex < tabs.TabCount - 1) tabs.SelectedIndex++; };
            tabs.SelectedIndexChanged += (s, e) => UpdateTaskNavigation();
            review.Text = T("Mark for review", "Đánh dấu xem lại");
            review.CheckedChanged += (s, e) => { if (!updatingReview && SelectedTaskKey() != null) marks[SelectedTaskKey()] = review.Checked; };
            start.Text = T("Start", "Bắt đầu bài thi"); restart.Text = T("Restart Project", "Làm lại project"); submit.Text = T("Submit Test", "Nộp bài"); offlineSubmit.Text = T("Submit", "Nộp bài"); recover.Text = T("Recover exam", "Khôi phục bài thi");
            rules.Text = T("50 minutes. 7 projects, ", "50 phút. 7 project, ") + prepared.Definition.N + T(" tasks.\n", " task.\n") +
                T("A random subset of tasks is selected. Total score: 1000; pass: 700. No grading feedback during the exam.\nNext Project is one-way. Restart affects the current project only; time keeps running. Submit is available at any time.\nNetwork loss pauses the timer for up to 30 minutes. Crash downtime counts; resume is offered within 15 minutes.",
                  "Các task được chọn ngẫu nhiên. Tổng điểm: 1000; đạt từ 700. Không hiển thị đúng/sai khi đang thi.\nChuyển project chỉ một chiều. Làm lại chỉ áp dụng project hiện tại; thời gian vẫn chạy. Có thể nộp bài bất kỳ lúc nào.\nMất mạng tạm dừng tối đa 30 phút. Thời gian ứng dụng đóng vẫn tính; có thể khôi phục trong 15 phút.");
            offline.Controls.Add(new Label { Text = T("Connection lost – waiting to reconnect", "Mất kết nối – đang chờ kết nối lại"), Dock = DockStyle.Fill, Font = new Font("Segoe UI", 18, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
            offlineSubmit.Dock = DockStyle.Bottom; offline.Controls.Add(offlineSubmit);
            start.Click += async (s, e) => await Command(() => host.StartAsync(), false);
            next.Click += async (s, e) => { if (snapshot?.CurrentIndex == 6) await Submit(); else if (Confirm(T("You will not be able to return to this project.", "Bạn sẽ không thể quay lại project này"))) await Command(() => host.InvokeAsync(c => { c.Next(); return true; }), true); };
            restart.Click += async (s, e) => { if (Confirm(T("Restart the current project? Time keeps running.", "Làm lại project hiện tại? Thời gian vẫn chạy."))) await Command(() => host.InvokeAsync(c => { c.Restart(); return true; }), false); };
            submit.Click += async (s, e) => await Submit(); offlineSubmit.Click += async (s, e) => await Submit();
            recover.Click += (s, e) => { allowClose = true; Close(); };
            if (host != null) { host.StateChanged += HostState; host.Warning += HostWarning; host.WordDialogRequired += BusyWarning; }
            Shown += async (s, e) =>
            {
                Arrange();
                if (resume && host != null) await Command(() => host.ResumeAsync(), true);
                else if (host != null)
                {
                    try { await host.PrewarmAsync(); }
                    catch (Exception ex) { AppLogger.Write("Testing rules prewarm failed; Start will retry: " + ex.Message); }
                }
            };
            Resize += (s, e) => Arrange();
            ApplyState(new TestingSessionSnapshot(TestingSessionStatus.Ready, TestingFinalizeReason.None, 0, TimeSpan.Zero,
                Enumerable.Repeat(TestingProjectState.NotStarted, 7), new ExamTaskGrade[0], null));
        }
        private void Dispatch(Action action)
        { if (IsDisposed || Disposing || !IsHandleCreated) return; if (InvokeRequired) { try { BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing && IsHandleCreated) action(); })); } catch (InvalidOperationException) { } } else action(); }
        private void HostState(TestingSessionSnapshot value) => Dispatch(() => ApplyState(value));
        internal void HostWarning(TestingWarningCode code) => Dispatch(() =>
        {
            if (code == TestingWarningCode.DocumentReopened) { status.Text = T("Word was reopened; the timer keeps running.", "Đã mở lại Word; thời gian vẫn chạy."); Arrange(); }
            else if (code == TestingWarningCode.RecoveryRequired) { RecoveryRequired = true; recover.Visible = true; HideProgress(); status.Text = T("Exam recovery is required; saved work is retained.", "Bài thi cần khôi phục; dữ liệu đã được giữ"); ApplyState(snapshot); }
            else status.Text = T("Word is reconnecting; saved work is retained and retry continues.", "Đang kết nối lại Word; bài đã lưu được giữ và sẽ tiếp tục thử lại.");
        });
        private void BusyWarning() => Dispatch(() => status.Text = T("Close the open Word dialog, then try again.", "Hãy đóng hộp thoại đang mở trong Word rồi bấm lại."));
#if DEBUG
        private async Task ChangeDebugConnectivity()
        {
            if (operation || host?.CanSimulateOffline != true || RecoveryRequired ||
                (snapshot.Status != TestingSessionStatus.Running && snapshot.Status != TestingSessionStatus.Offline)) return;
            operation = true; ApplyState(snapshot);
            try { await host.SetDebugOfflineAsync(!host.IsDebugOffline).ConfigureAwait(false); }
            catch (Exception ex)
            {
                AppLogger.Write("Debug connectivity simulation failed", ex);
                Dispatch(() => status.Text = T("Offline simulation failed; try again.", "Giả lập mất mạng thất bại; hãy thử lại."));
            }
            finally
            {
                Dispatch(() =>
                {
                    status.Text = host.IsDebugOffline ? T("DEBUG: Simulated offline", "DEBUG: Đang giả lập mất mạng") : T("DEBUG: Online", "DEBUG: Đã kết nối");
                    operation = false; ApplyState(snapshot);
                });
            }
        }
#endif
        internal void ApplyState(TestingSessionSnapshot value)
        {
            var previous = snapshot; snapshot = value;
            int seconds = (int)Math.Max(0, Math.Ceiling((TestingSession.Duration - value.Elapsed).TotalSeconds));
            string time = (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
            if (countdown.Text != time) countdown.Text = time;
            // A timer observation must not touch AutoSize bars, z-order or navigation.
            if (previous != null && previous.Status == value.Status && previous.CurrentIndex == value.CurrentIndex &&
                renderedOperation == operation && renderedRecovery == RecoveryRequired && value.Status != TestingSessionStatus.Completed) return;
            bool layoutChanged = previous == null || previous.Status != value.Status || previous.CurrentIndex != value.CurrentIndex || renderedRecovery != RecoveryRequired;
            renderedOperation = operation; renderedRecovery = RecoveryRequired;
            var layout = Controls[0]; SuspendLayout(); layout.SuspendLayout();
            try
            {
                SetText(project, "Project " + (value.CurrentIndex + 1) + "/7");
                bool ready = value.Status == TestingSessionStatus.Ready, running = value.Status == TestingSessionStatus.Running;
                bool lost = value.Status == TestingSessionStatus.Offline;
                SetVisible(start, ready); SetEnabled(start, ready && !operation); SetVisible(rules, ready);
                SetText(next, value.CurrentIndex == 6 ? T("Submit", "Nộp bài") : T("Next Project →", "Project tiếp theo →"));
                SetEnabled(next, running && !operation && !RecoveryRequired); SetEnabled(restart, next.Enabled);
                SetEnabled(submit, (running || lost) && !operation && !RecoveryRequired); SetEnabled(offlineSubmit, submit.Enabled);
                SetVisible(offline, lost); SetVisible(tabs, running && !RecoveryRequired);
                if (lost) offline.BringToFront(); else if (ready) rules.BringToFront(); else tabs.BringToFront();
                if (!ready && value.Status != TestingSessionStatus.Completed)
                {
                    string id = prepared.Definition.Projects[value.CurrentIndex].ProjectId;
                    if (shownProject != id) { BuildTabs(id); shownProject = id; }
                }
                UpdateTaskNavigation();
            }
            finally { layout.ResumeLayout(layoutChanged); ResumeLayout(layoutChanged); }
            if (layoutChanged) Arrange(); // Only a project/interaction-state transition reaches this path.
            if (value.Status == TestingSessionStatus.Submitting && !RecoveryRequired) ShowProgress();
            if (value.Status == TestingSessionStatus.Completed && !resultShown && host != null) { resultShown = true; ShowResult(); }
        }
        private static void SetText(Control control, string text) { if (control.Text != text) control.Text = text; }
        private static void SetEnabled(Control control, bool enabled) { if (control.Enabled != enabled) control.Enabled = enabled; }
        private static void SetVisible(Control control, bool visible) { if (control.Visible != visible) control.Visible = visible; }
        private void BuildTabs(string id)
        {
            shownProject = id;
            while (tabs.TabCount > 0) { var page = tabs.TabPages[0]; tabs.TabPages.Remove(page); page.Dispose(); }
            var selected = prepared.Definition.Projects.Single(p => p.ProjectId == id); var package = packages.Single(p => p.Meta.ProjectId == id);
            projectId.Text = "Project: " + id;
            foreach (string taskId in selected.TaskIds)
            {
                var task = package.Tasks.Single(t => t.TaskId == taskId);
                var tab = new TabPage("Task " + (tabs.TabCount + 1));
                tab.Controls.Add(new RichTextBox { Name = "Instruction", Text = package.Lang[task.InstructionKey], ReadOnly = true,
                    Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.White, WordWrap = true,
                    ScrollBars = RichTextBoxScrollBars.Vertical, Font = new Font("Segoe UI", 18) });
                tabs.TabPages.Add(tab);
            }
            UpdateTaskNavigation();
        }
        private string SelectedTaskKey()
        {
            if (shownProject == null || tabs.SelectedIndex < 0) return null;
            var selected = prepared.Definition.Projects.Single(p => p.ProjectId == shownProject);
            return tabs.SelectedIndex < selected.TaskIds.Count ? shownProject + "/" + selected.TaskIds[tabs.SelectedIndex] : null;
        }
        private void UpdateTaskNavigation()
        {
            bool active = snapshot?.Status == TestingSessionStatus.Running && !operation && !RecoveryRequired;
            SetEnabled(previousTask, active && tabs.SelectedIndex > 0); SetEnabled(nextTask, active && tabs.SelectedIndex < tabs.TabCount - 1);
            SetEnabled(review, active);
            bool visible = tabs.TabCount > 0 && snapshot?.Status == TestingSessionStatus.Running && !RecoveryRequired;
            SetVisible(review, visible); SetVisible(taskCounter, visible);
            SetText(taskCounter, "Task " + (tabs.SelectedIndex + 1) + "/" + tabs.TabCount);
            string key = SelectedTaskKey(); updatingReview = true;
            try { string name = key == null ? "MarkForReview" : "Mark_" + key.Substring(key.IndexOf('/') + 1); if (review.Name != name) review.Name = name; bool marked = key != null && marks.ContainsKey(key) && marks[key]; if (review.Checked != marked) review.Checked = marked; }
            finally { updatingReview = false; }
        }
#if DEBUG
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Shift | Keys.F12)) { _ = ChangeDebugConnectivity(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
#endif
        private bool Confirm(string text) => ConfirmChoice != null ? ConfirmChoice(text) : MessageBox.Show(this, text, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        private async Task Submit() { if (Confirm(T("Submit this exam?", "Nộp bài thi này?"))) await Command(() => host.SubmitAsync(), true); }
        private async Task Command(Func<Task<bool>> work, bool grading)
        {
            if (operation || host == null || snapshot.Status == TestingSessionStatus.Completed || snapshot.Status == TestingSessionStatus.Submitting) return; operation = true; if (grading) ShowProgress(); ApplyState(snapshot);
            try { await work().ConfigureAwait(false); Dispatch(() => { status.Text = ""; Arrange(); }); }
            catch (Exception ex)
            {
                AppLogger.Write("Testing UI command failed", ex);
                Dispatch(() =>
                {
                    if (WordBusyException.IsBusy(ex)) BusyWarning();
                    else if (snapshot.Status == TestingSessionStatus.Ready) status.Text = ex.Message;
                    else if (!RecoveryRequired)
                    {
                        status.Text = T("Please try again; saved work is retained.", "Hãy thử lại; bài đã lưu được giữ.");
                    }
                });
            }
            finally { Dispatch(() => { operation = false; if (snapshot.Status != TestingSessionStatus.Submitting) HideProgress(); ApplyState(snapshot); }); }
        }

        private void ShowProgress() { if (progress == null && IsHandleCreated && host != null) { progress = new GradingProgress(prepared.Language); progress.Show(this); } }
        private void HideProgress() { progress?.Close(); progress?.Dispose(); progress = null; }
        private async void ShowResult()
        {
            try
            {
                var result = await host.InvokeAsync(c => c.Session.Result).ConfigureAwait(false);
                Dispatch(() =>
                {
                    HideProgress();
                    if (DisplayResult != null) DisplayResult(result);
                    else using (var window = new ResultForm(result, packages, prepared.Language)) window.ShowDialog(this);
                    allowClose = true; Close();
                });
            }
            catch (Exception ex) { AppLogger.Write("Testing result display failed", ex); Dispatch(() => { HideProgress(); resultShown = false; status.Text = T("Result display will retry; the saved result is retained.", "Sẽ thử hiển thị lại kết quả; kết quả đã lưu được giữ."); }); }
        }

        private async void Arrange()
        {
            if (arranging || IsDisposed || WindowState == FormWindowState.Minimized) return; arranging = true;
            try
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                int height = UiLayout.PanelHeight(this, area); Bounds = new Rectangle(area.Left, area.Bottom - height, area.Width, height);
                if (host != null && snapshot != null && snapshot.Status != TestingSessionStatus.Ready && snapshot.Status != TestingSessionStatus.Completed)
                    await host.InvokeAsync(c => { c.SetWindowBounds(area.Left, area.Top, area.Width, area.Height - height); return true; });
            }
            catch (Exception ex) { AppLogger.Write("Testing placement deferred", ex); }
            finally { arranging = false; }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown) { skipDisposal = true; Program.DeferRecoveryToNextStart(); }
            else if (!allowClose && host != null && (operation || (snapshot.Status != TestingSessionStatus.Ready && snapshot.Status != TestingSessionStatus.Completed)))
            {
                e.Cancel = true;
                if (closeQueued) { base.OnFormClosing(e); return; }
                closeQueued = true;
                BeginInvoke((Action)(async () =>
                {
                    if (operation || RecoveryRequired)
                    {
                        if (Confirm(T("Close anyway? Saved data will be recovered on the next start.", "Đóng ngay? Dữ liệu đã lưu sẽ được khôi phục ở lần mở sau.")))
                        { skipDisposal = true; Program.DeferRecoveryToNextStart(); allowClose = true; Close(); }
                    }
                    else await Submit();
                    closeQueued = false;
                }));
            }
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                HideProgress();
                if (host != null) { host.StateChanged -= HostState; host.Warning -= HostWarning; host.WordDialogRequired -= BusyWarning;
                    if (!skipDisposal) try { host.Dispose(); } catch (Exception ex) { AppLogger.Write("Testing disposal deferred", ex); } }
            }
            base.Dispose(disposing);
        }
    }
}
