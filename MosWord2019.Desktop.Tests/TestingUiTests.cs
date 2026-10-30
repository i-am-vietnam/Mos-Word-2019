using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MosWord2019.Core.Interfaces;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TestingUiTests
    {
        public TestContext TestContext { get; set; }
        [TestInitialize] public void TraceStart() { TraceCase("start"); }
        [TestCleanup] public void TraceEnd() { TraceCase("end"); }
        private void TraceCase(string phase)
        {
            string folder = Environment.GetEnvironmentVariable("B3_UI_EVIDENCE");
            if (!string.IsNullOrEmpty(folder)) File.AppendAllText(Path.Combine(folder, "cases.log"), phase + " " + TestContext.TestName + Environment.NewLine);
        }
        private static T Find<T>(Control form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();
        [TestMethod]
        public void LoginOffersBothModes() => Sta(() => { using (var form = new MosWord2019.LoginForm()) Assert.IsTrue(Find<ComboBox>(form, "Mode").Items.Contains(MosWord2019.AppMode.Testing)); });
        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void TestingButtonsTabsCountdownAndOfflineOverlayFollowState(string language)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json")).Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    using (var form = new MosWord2019.TestingForm(prepared, packages))
                    {
                        form.Show(); Application.DoEvents();
                        Assert.IsTrue(Find<Button>(form, "Start").Enabled); Capture(form, "start-" + language);
                        Assert.IsFalse(Find<Button>(form, "NextProject").Enabled);
                        Assert.AreEqual(0, form.Controls.Find("Grade", true).Length); Assert.AreEqual(0, form.Controls.Find("Previous", true).Length);
                        var states = Enumerable.Repeat(TestingProjectState.NotStarted, 7).ToArray(); states[0] = TestingProjectState.Active;
                        Func<TestingSessionStatus, int, TimeSpan, TestingSessionSnapshot> snapshot = (status, index, elapsed) => new TestingSessionSnapshot(status, TestingFinalizeReason.None, index, elapsed, states, new ExamTaskGrade[0], null);
                        form.ApplyState(snapshot(TestingSessionStatus.Running, 0, TimeSpan.FromMinutes(45)));
                        var tabs = Find<TabControl>(form, "ExamTasks"); Assert.AreEqual(prepared.Definition.Projects[0].TaskIds.Count, tabs.TabCount);
                        Assert.AreEqual(Color.Red, Find<Label>(form, "Countdown").ForeColor);
                        var mark = Find<CheckBox>(form, "Mark_" + prepared.Definition.Projects[0].TaskIds[0]); mark.Checked = true;
                        form.ApplyState(snapshot(TestingSessionStatus.Running, 0, TimeSpan.FromMinutes(46))); Assert.IsTrue(mark.Checked);
                        Assert.IsTrue(Find<Button>(form, "NextProject").Enabled); Capture(form, "active-" + language);
                        form.ApplyState(snapshot(TestingSessionStatus.Offline, 0, TimeSpan.FromMinutes(46)));
                        Assert.IsFalse(tabs.Visible); Assert.IsTrue(Find<Panel>(form, "OfflineOverlay").Visible); Capture(form, "offline-" + language);
                        Assert.IsFalse(Find<Button>(form, "RestartProject").Enabled); Assert.IsTrue(Find<Button>(form, "OfflineSubmit").Enabled);
                        form.ApplyState(snapshot(TestingSessionStatus.Running, 6, TimeSpan.FromMinutes(46)));
                        Assert.AreEqual(language == "vi" ? "Nộp bài" : "Submit", Find<Button>(form, "NextProject").Text);
                        form.ApplyState(snapshot(TestingSessionStatus.Submitting, 6, TimeSpan.FromMinutes(50)));
                        Assert.IsFalse(Find<Button>(form, "Submit").Enabled); Assert.IsFalse(Find<Button>(form, "NextProject").Enabled);
                        form.Hide();
                    }
                }
            });
        }
        [DataTestMethod]
        [DataRow("en", true)]
        [DataRow("en", false)]
        [DataRow("vi", true)]
        [DataRow("vi", false)]
        public void ResultContainsScoresAndFullInstructionsButNoDiagnostics(string language, bool pass)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json")).Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    var grades = prepared.Definition.Projects.SelectMany(p => p.TaskIds.Select(t => new ExamTaskGrade(p.ProjectId, t,
                        new TaskGradeResult(pass ? TaskGradeOutcome.Pass : TaskGradeOutcome.Error, "PRIVATE_DIAGNOSTIC", "", t))));
                    var result = new ExamScorer(_ => { }).Score(prepared.Definition, grades);
                    using (var form = new MosWord2019.ResultForm(result, packages, language))
                    {
                        Assert.AreEqual((pass ? "1000" : "0") + " / 1000", Find<Label>(form, "TotalScore").Text);
                        Assert.AreEqual(pass ? (language == "vi" ? "Đạt" : "Pass") : (language == "vi" ? "Cần Luyện Tập thêm" : "Needs more practice"), Find<Label>(form, "Verdict").Text);
                        var wrong = Find<TextBox>(form, "WrongTasks"); Assert.IsFalse(wrong.Text.Contains("PRIVATE_DIAGNOSTIC"));
                        Assert.AreEqual(pass ? 0 : prepared.Definition.N, wrong.Lines.Length);
                        if (!pass)
                        {
                            var first = result.WrongTasks[0]; var package = packages.Single(p => p.Meta.ProjectId == "Word2019_" + first.ProjectCode);
                            Assert.AreEqual(first.ProjectCode + "-" + first.TaskId + ": " + package.Lang[first.InstructionKey].Replace("\r", " ").Replace("\n", " "), wrong.Lines[0]);
                        }
                        Assert.AreEqual(7, Find<ListView>(form, "ProjectScores").Items.Count);
                        Capture(form, "result-" + language + "-" + pass);
                    }
                }
            });
        }
        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void ButtonsDriveStaSessionWithConfirmationAndRetryableBusy(string language)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"));
                    var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    var network = new AlwaysOnlineMonitor(); UiRuntime runtime = null;
                    using (var host = new MosWord2019.TestingStaHost(() =>
                    {
                        var clock = new StopwatchTestingClock(); runtime = new UiRuntime(prepared.Definition);
                        return new TestingSessionCoordinator(prepared, new TestingSession(prepared.SessionId, prepared.Definition, clock, network), store, clock, runtime, () => { }, _ => { });
                    }, network, _ => { }))
                    using (var form = new MosWord2019.TestingForm(prepared, packages, host))
                    {
                        bool confirm = false; ExamResult result = null;
                        form.ConfirmChoice = _ => confirm; form.DisplayResult = value => result = value;
                        form.Show(); Find<Button>(form, "Start").PerformClick();
                        Pump(() => Find<Button>(form, "NextProject").Enabled);
                        Find<Button>(form, "NextProject").PerformClick(); Application.DoEvents();
                        Assert.AreEqual("Project 1/7", Find<Label>(form, "ProjectIndex").Text);
                        confirm = true; runtime.Busy = true; Find<Button>(form, "NextProject").PerformClick();
                        Pump(() => Find<Label>(form, "ExamStatus").Text.Contains(language == "vi" ? "hộp thoại" : "dialog") && Find<Button>(form, "NextProject").Enabled);
                        Assert.IsTrue(Find<Button>(form, "NextProject").Enabled);
                        Find<Button>(form, "NextProject").PerformClick();
                        Pump(() => Find<Label>(form, "ProjectIndex").Text == "Project 2/7" && Find<Button>(form, "RestartProject").Enabled);
                        Find<Button>(form, "RestartProject").PerformClick(); Pump(() => runtime.Restarts == 1 && Find<Button>(form, "Submit").Enabled);
                        Find<Button>(form, "Submit").PerformClick(); Pump(() => result != null && form.IsDisposed);
                        Assert.AreEqual(0, result.Total); Assert.AreEqual(prepared.Definition.N, result.WrongTasks.Count);
                        Assert.IsFalse(File.Exists(store.StatePath));
                    }
                }
            });
        }
        [TestMethod]
        public void StartupProgressPumpsUiWhileBackgroundGradingRuns()
        {
            Sta(() =>
            {
                int ticks = 0;
                using (var timer = new System.Windows.Forms.Timer { Interval = 10 })
                {
                    timer.Tick += (s, e) => ticks++; timer.Start();
                    int value = MosWord2019.GradingProgress.Run("vi", async () => { await Task.Delay(150); return 7; });
                    Assert.AreEqual(7, value); Assert.IsTrue(ticks > 0);
                }
            });
        }
        [TestMethod]
        public void RecoveryReloadsInstructionsInPersistedLanguage()
        {
            using (var folder = new TestWorkspace())
            {
                var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "vi");
                var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json")).Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, "vi");
                var loaded = MosWord2019.TestingFlow.LoadForExam(TestCaseData.ProjectsRoot, prepared);
                foreach (var project in prepared.Definition.Projects)
                {
                    var expected = packages.Single(p => p.Meta.ProjectId == project.ProjectId);
                    var actual = loaded.Single(p => p.Meta.ProjectId == project.ProjectId);
                    foreach (var id in project.TaskIds)
                    { var task = expected.Tasks.Single(t => t.TaskId == id); Assert.AreEqual(expected.Lang[task.InstructionKey], actual.Lang[task.InstructionKey]); }
                }
            }
        }
        [TestMethod]
        public void ClosingDuringStartWaitsForExplicitDeferredExitChoice()
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                using (var gate = new ManualResetEventSlim(false))
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en");
                    var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"));
                    var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, "en");
                    var network = new AlwaysOnlineMonitor(); var runtime = new UiRuntime(prepared.Definition) { EntryGate = gate };
                    using (var host = new MosWord2019.TestingStaHost(() =>
                    {
                        var clock = new StopwatchTestingClock();
                        return new TestingSessionCoordinator(prepared, new TestingSession(prepared.SessionId, prepared.Definition, clock, network), store, clock, runtime, () => { }, _ => { });
                    }, network, _ => { }))
                    using (var form = new MosWord2019.TestingForm(prepared, packages, host))
                    {
                        bool prompted = false; ExamResult result = null;
                        form.ConfirmChoice = _ => { prompted = true; return false; }; form.DisplayResult = r => result = r;
                        try
                        {
                            form.Show(); Find<Button>(form, "Start").PerformClick(); Pump(() => runtime.Entering);
                            form.Close(); Pump(() => prompted); Assert.IsFalse(form.IsDisposed);
                        }
                        finally { gate.Set(); }
                        Pump(() => Find<Button>(form, "Submit").Enabled);
                        form.ConfirmChoice = _ => true; Find<Button>(form, "Submit").PerformClick(); Pump(() => result != null && form.IsDisposed);
                    }
                }
            });
        }
        [DataTestMethod]
        [DataRow(CloseReason.UserClosing, true, false)]
        [DataRow(CloseReason.TaskManagerClosing, false, true)]
        [DataRow(CloseReason.WindowsShutDown, false, true)]
        public void StartupGradingClosePolicyPreservesSystemShutdown(CloseReason reason, bool cancel, bool defer)
        {
            Sta(() =>
            {
                var flag = typeof(MosWord2019.Program).GetField("recoveryDeferred", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                object original = flag.GetValue(null); flag.SetValue(null, 0);
                bool? actualCancel = null, actualDefer = null;
                try
                {
                    using (var timer = new System.Windows.Forms.Timer { Interval = 10 })
                    {
                        timer.Tick += (s, e) =>
                        {
                            timer.Stop(); var window = Application.OpenForms.OfType<MosWord2019.GradingProgress>().Single();
                            var args = new FormClosingEventArgs(reason, false);
                            typeof(Form).GetMethod("OnFormClosing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(window, new object[] { args });
                            actualCancel = args.Cancel; actualDefer = MosWord2019.Program.RecoveryDeferred;
                        };
                        timer.Start(); MosWord2019.GradingProgress.Run("en", async () => { await Task.Delay(150); return true; });
                    }
                    Assert.AreEqual(cancel, actualCancel); Assert.AreEqual(defer, actualDefer);
                }
                finally { flag.SetValue(null, original); }
            });
        }
        private static void Pump(Func<bool> done)
        {
            var deadline = Stopwatch.StartNew();
            while (!done() && deadline.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(5); }
            Assert.IsTrue(done(), "UI command did not finish within ten seconds.");
        }
        private sealed class UiRuntime : ITestingProjectRuntime
        {
            private readonly ExamDefinition definition; private string current;
            internal volatile bool Busy, Entering; internal int Restarts; internal ManualResetEventSlim EntryGate;
            internal UiRuntime(ExamDefinition definition) { this.definition = definition; }
            public void Enter(string id, bool resume) { Entering = true; EntryGate?.Wait(); current = id; }
            public void AutosaveCurrent() { }
            public void Observe() { }
            public void SetInputEnabled(bool enabled) { }
            public void Restart(string id) { Restarts++; }
            public void Close() { }
            public void Dispose() { }
            public IReadOnlyList<ExamTaskGrade> GradeSaved(string id) => definition.Projects.Single(p => p.ProjectId == id).TaskIds
                .Select(t => new ExamTaskGrade(id, t, new TaskGradeResult(TaskGradeOutcome.Fail, "internal only", "", t))).ToList();
            public void LeaveAndGrade(Action<IReadOnlyList<ExamTaskGrade>> persist)
            { if (Busy) { Busy = false; throw new WordBusyException(new Exception("dialog")); } persist(GradeSaved(current)); }
        }
        private static void Capture(Form form, string name)
        {
            string folder = Environment.GetEnvironmentVariable("B3_UI_EVIDENCE"); if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder); form.Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, name + ".png")); }
        }
        private static void Sta(Action action)
        {
            Exception failure = null; var thread = new Thread(() =>
            {
                bool original = Control.CheckForIllegalCrossThreadCalls; Control.CheckForIllegalCrossThreadCalls = true;
                try { action(); } catch (Exception ex) { failure = ex; }
                finally { Control.CheckForIllegalCrossThreadCalls = original; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
