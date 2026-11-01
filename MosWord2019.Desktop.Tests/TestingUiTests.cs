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
        public void LoginOffersBothModes() => Sta(() => { using (var form = new MosWord2019.LoginForm(new MosWord2019.OfflineAuthenticationService(), null, _ => { })) Assert.IsFalse(Find<RadioButton>(form, "Testing").Checked); });
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
        [DataRow("en")]
        [DataRow("vi")]
        public void TransientTickWarningKeepsTheExamUsable(string language)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json")).Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    using (var form = new MosWord2019.TestingForm(prepared, packages))
                    using (var session = new TestingSession(prepared.SessionId, prepared.Definition, new StopwatchTestingClock(), new AlwaysOnlineMonitor()))
                    {
                        form.Show(); session.Start(); form.ApplyState(session.CaptureState());
                        form.HostWarning(MosWord2019.TestingWarningCode.ObservationDelayed);
                        Assert.IsFalse(form.RecoveryRequired);
                        Assert.IsTrue(Find<Button>(form, "NextProject").Enabled);
                        Assert.IsTrue(Find<Button>(form, "Submit").Enabled);
                        Assert.IsTrue(Find<TabControl>(form, "ExamTasks").Visible);
                        form.Hide();
                    }
                }
            });
        }

        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void RecoveryWarningDisablesExamOnlyAfterCoordinatorFault(string language)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json")).Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    using (var form = new MosWord2019.TestingForm(prepared, packages))
                    using (var session = new TestingSession(prepared.SessionId, prepared.Definition, new StopwatchTestingClock(), new AlwaysOnlineMonitor()))
                    {
                        form.Show(); session.Start(); form.ApplyState(session.CaptureState());
                        form.HostWarning(MosWord2019.TestingWarningCode.RecoveryRequired);
                        Assert.IsTrue(form.RecoveryRequired); Assert.IsTrue(Find<Button>(form, "Recover").Visible);
                        Assert.IsFalse(Find<Button>(form, "NextProject").Enabled); Assert.IsFalse(Find<Button>(form, "Submit").Enabled);
                        Assert.IsFalse(Find<TabControl>(form, "ExamTasks").Visible);
                        form.Hide();
                    }
                }
            });
        }
        [TestMethod]
        public void RecoveryDialogOffersThreeExplicitBilingualChoices()
        {
            Sta(() =>
            {
                foreach (string name in new[] { "Retry", "SubmitSaved", "Later" })
                {
                    MosWord2019.TestingRecoveryFailureChoice? selected = null;
                    using (var form = MosWord2019.TestingRecoveryDialog.Create(value => selected = value))
                    {
                        form.Show(); Application.DoEvents();
                        Assert.IsTrue(Find<Button>(form, "Retry").Text.Contains("Thử lại"));
                        Assert.IsTrue(Find<Button>(form, "SubmitSaved").Text.Contains("Submit from saved work"));
                        Assert.IsTrue(Find<Button>(form, "Later").Text.Contains("Để sau"));
                        Find<Button>(form, name).PerformClick(); Assert.IsNotNull(selected);
                        Assert.AreEqual(name, selected.Value.ToString());
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
                        var wrong = Find<TextBox>(form, "WrongTasks"); Assert.IsTrue(wrong.WordWrap); Assert.AreEqual(ScrollBars.Vertical, wrong.ScrollBars); Assert.IsFalse(wrong.Text.Contains("PRIVATE_DIAGNOSTIC"));
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
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public void FailedInspectOffersRetrySavedSubmitOrLater(int selected)
        {
            var choice = (MosWord2019.TestingRecoveryFailureChoice)selected;
            int attempts = 0, prompts = 0, finalizes = 0, logs = 0;
            bool done = MosWord2019.TestingRecoveryDialog.Run(
                () => { if (++attempts == 1) throw new IOException("inspection failure"); },
                () => finalizes++, error => { prompts++; return choice; }, error => logs++);
            Assert.AreEqual(choice != MosWord2019.TestingRecoveryFailureChoice.Later, done);
            Assert.AreEqual(choice == MosWord2019.TestingRecoveryFailureChoice.Retry ? 2 : 1, attempts);
            Assert.AreEqual(choice == MosWord2019.TestingRecoveryFailureChoice.SubmitSaved ? 1 : 0, finalizes);
            Assert.AreEqual(1, prompts); Assert.AreEqual(1, logs);
        }
        [TestMethod]
        public void FailedSavedFinalizationReturnsToTheSameRecoveryChoices()
        {
            int prompts = 0, finalized = 0;
            bool done = MosWord2019.TestingRecoveryDialog.Run(() => { throw new IOException("inspect"); },
                () => { finalized++; throw new IOException("saved file is locked"); },
                error => ++prompts == 1 ? MosWord2019.TestingRecoveryFailureChoice.SubmitSaved : MosWord2019.TestingRecoveryFailureChoice.Later,
                error => { });
            Assert.IsFalse(done); Assert.AreEqual(2, prompts); Assert.AreEqual(1, finalized);
        }
        [TestMethod]
        public void PrepareFailureUsesTheSameChoiceBeforeInspectingAgain()
        {
            bool inspected = false, finalized = false;
            bool done = MosWord2019.TestingRecoveryDialog.Run(() => inspected = true, () => finalized = true,
                error => MosWord2019.TestingRecoveryFailureChoice.SubmitSaved, error => { }, new IOException("An active Testing session requires resume/finalization."));
            Assert.IsTrue(done); Assert.IsTrue(finalized); Assert.IsFalse(inspected);
        }

        [DataTestMethod]
        [DataRow("en", false)]
        [DataRow("en", true)]
        [DataRow("vi", false)]
        [DataRow("vi", true)]
        public void OfflineSimulationUsesExistingSessionOverlayAndSubmissionOnlyInDebug(string language, bool waitForLimit)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var store = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"));
                    var prepared = store.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    var network = MosWord2019.TestingStaHost.CreateConnectivityMonitor();
                    var clock = new TestingSessionTests.FakeClock();
                    var runtime = new UiRuntime(prepared.Definition);
                    using (var host = new MosWord2019.TestingStaHost(() => new TestingSessionCoordinator(prepared,
                        new TestingSession(prepared.SessionId, prepared.Definition, clock, network), store, clock, runtime, () => { }, _ => { }), network, _ => { }))
                    using (var form = new MosWord2019.TestingForm(prepared, packages, host))
                    {
                        ExamResult result = null; form.ConfirmChoice = _ => true; form.DisplayResult = value => result = value;
                        form.Show(); Find<Button>(form, "Start").PerformClick(); Pump(() => Find<Button>(form, "Submit").Enabled);
#if DEBUG
                        Assert.AreEqual(0, form.Controls.Find("DebugSimulateOffline", true).Length);
                        Action toggle = () =>
                        {
                            var method = typeof(MosWord2019.TestingForm).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                            Assert.IsNotNull(method);
                            Assert.AreEqual(true, method.Invoke(form, new object[] { new Message(), Keys.Control | Keys.Shift | Keys.F12 }));
                        };
                        host.InvokeAsync(c => { clock.Advance(10); c.Tick(false); return true; }).GetAwaiter().GetResult();
                        toggle();
                        Pump(() => Find<Panel>(form, "OfflineOverlay").Visible && Find<Button>(form, "OfflineSubmit").Enabled);
                        Assert.IsFalse(runtime.InputEnabled); Assert.IsFalse(Find<TabControl>(form, "ExamTasks").Visible);
                        Assert.IsFalse(Find<Button>(form, "NextProject").Enabled); Assert.IsFalse(Find<Button>(form, "RestartProject").Enabled);
                        Assert.IsTrue(Find<Button>(form, "OfflineSubmit").Enabled);
                        Assert.IsTrue(MosWord2019.TestingStaHost.CreateConnectivityMonitor().IsOnline, "A separate exam must not inherit this simulation.");
                        var paused = host.InvokeAsync(c => { clock.Advance(120); c.Tick(false); return c.Session.Elapsed; }).GetAwaiter().GetResult();
                        Assert.AreEqual(TimeSpan.FromSeconds(10), paused);
                        toggle(); Pump(() => !Find<Panel>(form, "OfflineOverlay").Visible && Find<Button>(form, "Submit").Enabled);
                        Assert.IsTrue(runtime.InputEnabled); Assert.IsTrue(Find<TabControl>(form, "ExamTasks").Visible);
                        var resumed = host.InvokeAsync(c => { clock.Advance(5); c.Tick(false); return c.Session.Elapsed; }).GetAwaiter().GetResult();
                        Assert.AreEqual(TimeSpan.FromSeconds(15), resumed);
                        toggle(); Pump(() => Find<Panel>(form, "OfflineOverlay").Visible && Find<Button>(form, "OfflineSubmit").Enabled);
                        if (waitForLimit) host.InvokeAsync(c => { clock.Advance(1800); c.Tick(false); return true; }).GetAwaiter().GetResult();
                        else Find<Button>(form, "OfflineSubmit").PerformClick();
#else
                        Assert.AreEqual(0, form.Controls.Find("DebugSimulateOffline", true).Length);
                        Assert.IsNull(typeof(MosWord2019.TestingForm).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly));
                        Assert.IsTrue(network is AlwaysOnlineMonitor); Assert.IsTrue(network.IsOnline);
                        Find<Button>(form, "Submit").PerformClick();
#endif
                        Pump(() => result != null && form.IsDisposed);
                        Assert.IsFalse(File.Exists(store.StatePath));
                        var saved = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(folder.Folder, "Results", prepared.SessionId + ".json")));
                        Assert.AreEqual(prepared.SessionId, (string)saved["sessionId"]);
#if DEBUG
                        Assert.AreEqual(TimeSpan.FromSeconds(120).Ticks, (long)saved["offlinePeriods"][0]["DurationTicks"]);
                        Assert.AreEqual(waitForLimit ? "OfflineLimit" : "Submitted", (string)saved["finalizeReason"]);
#endif
                    }
                }
            });
        }

        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void LoginValidatesCredentialsLocalizesAndRoutesSelectedMode(string language)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    string path = Path.Combine(folder.Folder, "login.txt");
                    MosWord2019.AppSession selected = null;
                    using (var form = new MosWord2019.LoginForm(new MosWord2019.OfflineAuthenticationService(), new MosWord2019.LoginPreferences(path), value => selected = value))
                    {
                        form.Show();
                        Assert.IsTrue(Find<RadioButton>(form, "English").Checked);
                        Assert.IsTrue(Find<RadioButton>(form, "Training").Checked);
                        Find<RadioButton>(form, language == "vi" ? "Vietnamese" : "English").Checked = true;
                        Assert.AreEqual(language == "vi" ? "Tài khoản:" : "Username:", Find<Label>(form, "UsernameLabel").Text);
                        Assert.AreEqual(language == "vi" ? "Mật khẩu:" : "Password:", Find<Label>(form, "PasswordLabel").Text);
                        Assert.AreSame(Find<Button>(form, "Continue"), form.AcceptButton);
                        Assert.AreSame(Find<Button>(form, "Exit"), form.CancelButton);
                        Assert.IsTrue(Find<TextBox>(form, "Password").UseSystemPasswordChar);
                        foreach (string[] values in new[] { new[] { "", "" }, new[] { "name", "" }, new[] { "", "secret" }, new[] { " ", " " } })
                        {
                            Find<TextBox>(form, "Username").Text = values[0]; Find<TextBox>(form, "Password").Text = values[1];
                            Find<Button>(form, "Continue").PerformClick(); Assert.IsNull(selected);
                            Assert.AreEqual(language == "vi" ? "Vui lòng nhập tài khoản và mật khẩu." : "Please enter your username and password.", Find<Label>(form, "LoginStatus").Text);
                        }
                        Capture(form, "login-error-" + language);
                        foreach (string mode in new[] { "Training", "Testing" })
                        {
                            Find<RadioButton>(form, mode).Checked = true;
                            Find<TextBox>(form, "Username").Text = "learner"; Find<TextBox>(form, "Password").Text = "private-password";
                            Find<Button>(form, "Continue").PerformClick();
                            Assert.AreEqual(mode, selected.Mode.ToString()); Assert.AreEqual(language, selected.Language);
                            Assert.AreEqual("", Find<TextBox>(form, "Password").Text);
                        }
                        Assert.IsFalse(File.ReadAllText(path).Contains("private-password")); Capture(form, "login-" + language);
                    }
                    using (var restored = new MosWord2019.LoginForm(new MosWord2019.OfflineAuthenticationService(), new MosWord2019.LoginPreferences(path), _ => { }))
                    {
                        Assert.AreEqual("learner", Find<TextBox>(restored, "Username").Text);
                        Assert.IsTrue(Find<RadioButton>(restored, language == "vi" ? "Vietnamese" : "English").Checked);
                        Assert.AreEqual("", Find<TextBox>(restored, "Password").Text);
                    }
                    File.WriteAllText(path, "corrupt");
                    var preferences = new MosWord2019.LoginPreferences(path); preferences.Load(); Assert.AreEqual("en", preferences.Language);
                    var blocked = new MosWord2019.LoginPreferences(folder.Folder); blocked.Load(); blocked.Save();
                }
            });
        }

        [DataTestMethod]
        [DataRow("en", 1366, 768, 1f)]
        [DataRow("vi", 1366, 768, 1.25f)]
        [DataRow("vi", 1366, 768, 1.5f)]
        [DataRow("en", 1920, 1080, 1.25f)]
        [DataRow("en", 1920, 1080, 1.5f)]
        [DataRow("vi", 1920, 1080, 1f)]
        [DataRow("vi", 1366, 768, 1f)]
        [DataRow("en", 1366, 768, 1.25f)]
        [DataRow("en", 1366, 768, 1.5f)]
        [DataRow("vi", 1920, 1080, 1.25f)]
        [DataRow("vi", 1920, 1080, 1.5f)]
        [DataRow("en", 1920, 1080, 1f)]
        public void TestingLayoutHasCenteredRedTimerSequentialTabsAndTaskNavigation(string language, int width, int height, float scale)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json")).Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    using (var form = new MosWord2019.TestingForm(prepared, packages))
                    {
                        form.Show(); form.ApplyState(new TestingSessionSnapshot(TestingSessionStatus.Running, TestingFinalizeReason.None, 0, TimeSpan.Zero,
                            Enumerable.Repeat(TestingProjectState.Active, 7), new ExamTaskGrade[0], null));
                        typeof(MosWord2019.TestingForm).GetField("arranging", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(form, true);
                        form.AutoScaleMode = AutoScaleMode.None;
                        form.Scale(new SizeF(scale, scale));
                        form.Size = new Size(width, (int)((height - 40) * .35)); Application.DoEvents();
                        Assert.IsTrue(form.Height <= (int)((height - 40) * .35), "Panel exceeds 35% cap.");
                        var timer = Find<Label>(form, "Countdown"); var top = Find<TableLayoutPanel>(form, "ExamTopBar");
                        Assert.AreEqual(Color.Red, timer.ForeColor); Assert.IsTrue(timer.Font.Bold); Assert.IsTrue(timer.Font.Size >= 22);
                        Assert.IsTrue(Math.Abs(timer.Left + timer.Width / 2 - top.ClientSize.Width / 2) <= 3);
                        Assert.AreEqual("Project 1/7", Find<Label>(form, "ProjectIndex").Text);
                        var tabs = Find<TabControl>(form, "ExamTasks");
                        for (int i = 0; i < tabs.TabCount; i++) Assert.AreEqual("Task " + (i + 1), tabs.TabPages[i].Text);
                        Assert.AreEqual(0, form.Controls.Find("DebugSimulateOffline", true).Length);
                        var submitButton = Find<Button>(form, "Submit");
                        Assert.IsTrue(submitButton.ClientSize.Height >= TextRenderer.MeasureText(submitButton.Text, submitButton.Font).Height + 6, "Submit text is vertically clipped: height=" + submitButton.Height + " text=" + TextRenderer.MeasureText(submitButton.Text, submitButton.Font).Height + " parent=" + submitButton.Parent.Bounds + " rows=" + string.Join(",", ((TableLayoutPanel)submitButton.Parent).GetRowHeights()));
                        var instruction = (RichTextBox)tabs.TabPages[0].Controls[0];
                        Assert.IsTrue(instruction.Font.Size >= 18); Assert.IsTrue(instruction.WordWrap && instruction.ReadOnly);
                        Assert.AreEqual(BorderStyle.None, instruction.BorderStyle); Assert.IsTrue(instruction.ClientSize.Height >= 25);
                        Assert.IsFalse(Find<Button>(form, "PreviousTask").Enabled);
                        Find<Button>(form, "NextTask").PerformClick(); Assert.AreEqual(1, tabs.SelectedIndex);
                        Assert.AreEqual("Task 2/" + tabs.TabCount, Find<Label>(form, "TaskCounter").Text);
                        Find<Button>(form, "PreviousTask").PerformClick(); Assert.AreEqual(0, tabs.SelectedIndex);
                        tabs.SelectedIndex = tabs.TabCount - 1; Assert.IsFalse(Find<Button>(form, "NextTask").Enabled);
                        tabs.SelectedIndex = 0;
                        foreach (string name in new[] { "PreviousTask", "NextTask", "RestartProject", "NextProject", "Submit", "TaskCounter" })
                        {
                            Control control = form.Controls.Find(name, true).Single();
                            var bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                            Assert.IsTrue(form.ClientRectangle.Contains(bounds), name + " clipped at " + width + "/" + scale + " " + bounds + " within " + form.ClientRectangle);
                        }
                        Capture(form, "layout-" + language + "-" + width + "-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            });
        }

        [DataTestMethod]
        [DataRow("en", 1366, 768, 1f)]
        [DataRow("en", 1366, 768, 1.25f)]
        [DataRow("en", 1366, 768, 1.5f)]
        [DataRow("en", 1920, 1080, 1f)]
        [DataRow("en", 1920, 1080, 1.25f)]
        [DataRow("en", 1920, 1080, 1.5f)]
        [DataRow("vi", 1366, 768, 1f)]
        [DataRow("vi", 1366, 768, 1.25f)]
        [DataRow("vi", 1366, 768, 1.5f)]
        [DataRow("vi", 1920, 1080, 1f)]
        [DataRow("vi", 1920, 1080, 1.25f)]
        [DataRow("vi", 1920, 1080, 1.5f)]
        public void LoginAndResultDialogsFitTheViewport(string language, int width, int height, float scale)
        {
            Sta(() =>
            {
                using (var login = new MosWord2019.LoginForm(new MosWord2019.OfflineAuthenticationService(), null, _ => { }))
                {
                    login.Show(); Find<RadioButton>(login, language == "vi" ? "Vietnamese" : "English").Checked = true;
                    login.AutoScaleMode = AutoScaleMode.None; login.Scale(new SizeF(scale, scale)); Application.DoEvents();
                    Assert.IsTrue(login.Width <= width && login.Height <= height - 40);
                    Find<Button>(login, "Continue").PerformClick();
                    foreach (string name in new[] { "Username", "Password", "Continue", "Exit", "LoginStatus" })
                    {
                        var control = login.Controls.Find(name, true).Single();
                        Assert.IsTrue(login.ClientRectangle.Contains(login.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))), name + " clipped");
                    }
                    Assert.IsTrue(Find<Label>(login, "LoginStatus").Height >= login.Font.Height);
                    Capture(login, "login-layout-" + language + "-" + width + "-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json")).Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    var result = new ExamScorer(_ => { }).Score(prepared.Definition, new ExamTaskGrade[0]);
                    using (var form = new MosWord2019.ResultForm(result, packages, language))
                    {
                        form.Show(); form.AutoScaleMode = AutoScaleMode.None; form.Scale(new SizeF(scale, scale));
                        form.Size = new Size(Math.Min(width, form.Width), Math.Min(height - 40, form.Height)); Application.DoEvents();
                        Assert.IsTrue(form.Width <= width && form.Height <= height - 40);
                        var wrong = Find<TextBox>(form, "WrongTasks");
                        Assert.IsTrue(wrong.Height >= 30 && wrong.Font.Size >= 12 && wrong.WordWrap);
                        Assert.IsTrue(Find<Label>(form, "TotalScore").Font.Size >= 30);
                        Assert.AreEqual(Color.DarkOrange, Find<Label>(form, "Verdict").ForeColor);
                        Capture(form, "result-layout-" + language + "-" + width + "-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
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
            internal volatile bool Busy, Entering, InputEnabled; internal int Restarts; internal ManualResetEventSlim EntryGate;
            internal UiRuntime(ExamDefinition definition) { this.definition = definition; }
            public void Enter(string id, bool resume) { Entering = true; EntryGate?.Wait(); current = id; }
            public void AutosaveCurrent() { }
            public void Observe() { }
            public void SetInputEnabled(bool enabled) { InputEnabled = enabled; }
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
