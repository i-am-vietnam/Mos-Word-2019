using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Threading.Tasks;
using MosWord2019.Projects.OnlineSessions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class TrainingWarmupTests
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void NewOnlineTrainingUseOpensProjectOneOnceAfterWarmupAndKeepsManualSwitching(bool failOpen)
        {
            Sta(() =>
            {
                for (int use = 0; use < 2; use++)
                using (var folder = new TestWorkspace())
                using (var gate = new ManualResetEventSlim())
                using (var host = new TrainingWordController(() => new FakeWord { StartGate = gate, FailOpen = failOpen }, w => ((FakeWord)w).Alive))
                {
                    var clock = new OnlineClock(); Guid id = Guid.NewGuid();
                    using (var monitor = new SessionConnectivityMonitor(id, "Training", "token", new OnlineTransport(id, clock), clock))
                    using (var context = new OnlineSessionContext(new OnlineSessionCredentials { SessionId = id, Mode = "Training", Language = "vi" }, monitor, TestCaseData.ProjectsRoot, () => Task.CompletedTask))
                    {
                        monitor.PollAsync().GetAwaiter().GetResult();
                        var fake = TrainingWordController.Wait(host.InvokeAsync(w => (FakeWord)w));
                        int errors = 0;
                        using (var form = new MainForm(new AppSession(AppMode.Training, "vi", context), TestCaseData.ProjectsRoot,
                            new TrainingWorkspaceService(Path.Combine(folder.Folder, "Working"), Path.Combine(folder.Folder, "Documents"), Path.Combine(folder.Folder, "Pictures")),
                            host, notify: _ => errors++))
                        {
                            form.Show(); Application.DoEvents();
                            Assert.AreEqual(0, fake.Opens, "Auto-open must wait for pre-warm completion.");
                            ((ComboBox)form.Controls.Find("Projects", true).Single()).SelectedIndex = 1;
                            gate.Set();
                            PumpUntil(() => fake.Opens == 1);
                            var selector = (ComboBox)form.Controls.Find("Projects", true).Single();
                            Assert.AreEqual("Word2019_P01", ((ProjectPackage)selector.SelectedItem).Meta.ProjectId);
                            for (int i = 0; i < 4; i++) { typeof(MainForm).GetMethod("RefreshOnlineTraining", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null); Application.DoEvents(); }
                            Assert.AreEqual(1, fake.Opens, "An auto-open failure must not create a retry loop.");
                            Assert.AreEqual(failOpen ? 1 : 0, errors);
                            Assert.IsTrue(selector.Enabled);
                            var go = (Button)form.Controls.Find("Go", true).Single(); Assert.IsTrue(go.Enabled);
                            if (!failOpen)
                            {
                                selector.SelectedIndex = 1; go.PerformClick();
                                Assert.AreEqual(2, fake.Opens, "Manual project switching must remain available.");
                            }
                        }
                    }
                }
            });
        }

        private static void PumpUntil(Func<bool> condition)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && wait.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            Assert.IsTrue(condition(), "Training operation did not finish."); Application.DoEvents();
        }

        [TestMethod]
        public void ConfirmedOnlineCloseDuringPrewarmCancelsAutomaticProjectOpen()
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                using (var gate = new ManualResetEventSlim())
                using (var host = new TrainingWordController(() => new FakeWord { StartGate = gate }, w => ((FakeWord)w).Alive))
                {
                    var clock = new OnlineClock(); Guid id = Guid.NewGuid(); int ends = 0;
                    using (var monitor = new SessionConnectivityMonitor(id, "Training", "token", new OnlineTransport(id, clock), clock))
                    using (var context = new OnlineSessionContext(new OnlineSessionCredentials { SessionId = id, Mode = "Training", Language = "vi" }, monitor, TestCaseData.ProjectsRoot, () => { ends++; return Task.CompletedTask; }))
                    {
                        monitor.PollAsync().GetAwaiter().GetResult();
                        var fake = TrainingWordController.Wait(host.InvokeAsync(w => (FakeWord)w));
                        using (var form = new MainForm(new AppSession(AppMode.Training, "vi", context), TestCaseData.ProjectsRoot,
                            new TrainingWorkspaceService(Path.Combine(folder.Folder, "Working"), Path.Combine(folder.Folder, "Documents"), Path.Combine(folder.Folder, "Pictures")),
                            host, confirmRestart: _ => true, notify: _ => { }))
                        {
                            form.Show(); Application.DoEvents(); form.Close();
                            Assert.IsFalse(form.IsDisposed); Assert.AreEqual(0, fake.Opens);
                            gate.Set(); PumpUntil(() => form.IsDisposed);
                            Assert.AreEqual(0, fake.Opens, "A confirmed end must cancel auto-open before the pre-warm continuation.");
                            Assert.AreEqual(1, ends);
                        }
                    }
                }
            });
        }

        [DataTestMethod]
        [DataRow(20, false)]
        [DataRow(30, true)]
        public void TrainingReconnectKeepsTwentyMinuteOutageButEndsThirtyMinuteOutage(int minutes, bool ended)
        {
            Sta(() =>
            {
                using (var f = new OnlineFixture())
                {
                    f.Monitor.PollAsync().GetAwaiter().GetResult(); f.Refresh();
                    f.Clock.Now = TimeSpan.FromSeconds(90); f.Refresh();
                    f.Clock.Now += TimeSpan.FromMinutes(minutes);
                    f.Monitor.PollAsync().GetAwaiter().GetResult(); f.Refresh();
                    Assert.AreEqual(ended ? 1 : 0, f.Ends); Assert.AreEqual(ended, f.Form.IsDisposed);
                    if (ended) StringAssert.Contains(f.Context.TerminalMessage, "30 phút");
                    else { Assert.IsTrue(f.Monitor.IsOnline); Assert.IsTrue(f.Word.InputEnabled); }
                    Assert.AreEqual(0, f.Word.Saves);
                }
            });
        }
        [TestMethod]
        public void TrainingClockFormatsWholeHoursAndClampsExpiredAuthorization()
        {
            Assert.AreEqual("04:00:00", MainForm.FormatTrainingTime(TimeSpan.FromHours(4), true));
            Assert.AreEqual("03:59:59", MainForm.FormatTrainingTime(TimeSpan.FromSeconds(14398.2), true));
            Assert.AreEqual("00:00:00", MainForm.FormatTrainingTime(TimeSpan.FromSeconds(-1), true));
            Assert.AreEqual("02:07", MainForm.FormatTrainingTime(TimeSpan.FromMinutes(127), false));
        }

        [TestMethod]
        public void TrainingInputGateRunsOnlyOnOwnedControllerSta()
        {
            Sta(() =>
            {
                using (var host = new TrainingWordController(() => new FakeWord(), w => ((FakeWord)w).Alive))
                {
                    var fake = TrainingWordController.Wait(host.InvokeAsync(w => (FakeWord)w));
                    ((IWordInputLock)host).SetInputEnabled(false);
                    Assert.IsFalse(fake.InputEnabled);
                    ((IWordInputLock)host).SetInputEnabled(true);
                    Assert.IsTrue(fake.InputEnabled);
                    Assert.AreEqual(0, fake.Opens);
                    Assert.AreEqual(0, fake.Activations);
                }
            });
        }

        [TestMethod]
        public void TrainingOfflineBlocksActionsAndOwnedInputThenReconnectRestoresBoth()
        {
            Sta(() =>
            {
                using (var f = new OnlineFixture())
                {
                    Assert.IsFalse(f.Word.InputEnabled);
                    Assert.IsFalse(f.Form.Controls.Find("pnlTopBar", true).Single().Enabled);
                    Assert.IsTrue(f.Form.Controls.Find("OpeningProject", true).Single().Visible);
                    f.Monitor.PollAsync().GetAwaiter().GetResult(); f.Refresh();
                    Assert.IsTrue(f.Word.InputEnabled);
                    Assert.IsTrue(f.Form.Controls.Find("pnlTopBar", true).Single().Enabled);
                    Assert.IsFalse(f.Form.Controls.Find("OpeningProject", true).Single().Visible);
                    f.Clock.Now = TimeSpan.FromSeconds(90); f.Refresh();
                    Assert.IsFalse(f.Word.InputEnabled);
                    Assert.AreEqual("03:58:30", f.Form.Controls.Find("TrainingCountdown", true).Single().Text);
                    f.Monitor.PollAsync().GetAwaiter().GetResult(); f.Refresh();
                    Assert.IsTrue(f.Word.InputEnabled);
                    Assert.AreEqual(0, f.Ends);
                }
            });
        }

        [TestMethod]
        public void TrainingSelectorUsesVerifiedCatalogBeforeProjectDownloads()
        {
            Sta(() =>
            {
                var catalog = new[] {
                    new ProjectPackage { Meta = new ProjectMeta { ProjectId = "Word2019_P01" } },
                    new ProjectPackage { Meta = new ProjectMeta { ProjectId = "Word2019_P28" } }
                };
                using (var f = new OnlineFixture(catalog))
                {
                    var selector = (ComboBox)f.Form.Controls.Find("Projects", true).Single();
                    Assert.AreEqual(2, selector.Items.Count);
                    Assert.AreEqual("Project 1", ((ProjectPackage)selector.Items[0]).DisplayName);
                    Assert.AreEqual("Project 28", ((ProjectPackage)selector.Items[1]).DisplayName);
                    Assert.IsFalse(Directory.EnumerateFiles(f.Context.ProjectsRootPath, "*", SearchOption.AllDirectories).Any());
                    Assert.AreEqual(0, f.Word.Saves);
                }
            });
        }

        [DataTestMethod]
        [DataRow("expired")]
        [DataRow("rejected")]
        [DataRow("offline")]
        public void TrainingTerminalConditionsEndWithoutSaveOrExitConfirmation(string condition)
        {
            Sta(() =>
            {
                using (var f = new OnlineFixture())
                {
                    f.Monitor.PollAsync().GetAwaiter().GetResult(); f.Refresh();
                    if (condition == "expired") f.Clock.Now = TimeSpan.FromHours(4);
                    else if (condition == "rejected") { f.Transport.Reject = true; f.Monitor.PollAsync().GetAwaiter().GetResult(); }
                    else
                    {
                        f.Clock.Now = TimeSpan.FromSeconds(90); f.Refresh();
                        f.Clock.Now += TimeSpan.FromMinutes(30);
                    }
                    f.Refresh(); Application.DoEvents();
                    Assert.IsTrue(f.Form.IsDisposed);
                    Assert.AreEqual(1, f.Ends);
                    Assert.AreEqual(0, f.Confirms);
                    Assert.AreEqual(0, f.Word.Saves);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(f.Context.TerminalMessage));
                }
            });
        }

        [TestMethod]
        public void TrainingEarlyExitCancelKeepsUseThenConfirmedExitAwaitsEnd()
        {
            Sta(() =>
            {
                using (var f = new OnlineFixture())
                {
                    f.Monitor.PollAsync().GetAwaiter().GetResult(); f.Refresh();
                    f.Form.Close(); Assert.IsFalse(f.Form.IsDisposed); Assert.AreEqual(0, f.Ends);
                    Assert.AreEqual("Thoát sẽ kết thúc lượt sử dụng này. Bạn còn 04:00.", f.Confirmation);
                    f.Accept = true;
                    f.PendingEnd = new TaskCompletionSource<bool>();
                    f.Form.Close(); Application.DoEvents();
                    Assert.AreEqual(1, f.Ends); Assert.IsFalse(f.Form.IsDisposed);
                    Assert.AreEqual(0, f.Word.Saves);
                    f.PendingEnd.SetResult(true);
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    while (!f.Form.IsDisposed && wait.ElapsedMilliseconds < 3000) { Application.DoEvents(); Thread.Sleep(10); }
                    Assert.IsTrue(f.Form.IsDisposed);
                }
            });
        }

        [DataTestMethod]
        [DataRow(CloseReason.WindowsShutDown)]
        [DataRow(CloseReason.TaskManagerClosing)]
        public void TrainingInterruptedClosePreservesOnlineUseForResume(CloseReason reason)
        {
            Sta(() =>
            {
                using (var f = new OnlineFixture())
                {
                    typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(f.Form, new object[] { new FormClosingEventArgs(reason, false) });
                    Application.DoEvents();
                    f.Form.Dispose(); Application.DoEvents();
                    Assert.AreEqual(0, f.Ends); Assert.AreEqual(0, f.Confirms); Assert.IsNull(f.Context.TerminalMessage);
                }
            });
        }

        private sealed class OnlineFixture : IDisposable
        {
            private readonly TestWorkspace folder = new TestWorkspace();
            internal readonly OnlineClock Clock = new OnlineClock();
            internal readonly OnlineTransport Transport;
            internal readonly SessionConnectivityMonitor Monitor;
            internal readonly OnlineSessionContext Context;
            internal readonly OnlineWord Word = new OnlineWord();
            internal readonly MainForm Form;
            internal int Ends, Confirms;
            internal bool Accept;
            internal string Confirmation;
            internal TaskCompletionSource<bool> PendingEnd;
            internal OnlineFixture(ProjectPackage[] catalog = null)
            {
                Guid id = Guid.NewGuid(); Transport = new OnlineTransport(id, Clock);
                Monitor = new SessionConnectivityMonitor(id, "Training", "token", Transport, Clock);
                string packages = Path.Combine(folder.Folder, "Packages"); Directory.CreateDirectory(packages);
                Context = new OnlineSessionContext(new OnlineSessionCredentials { SessionId = id, Mode = "Training", Language = "vi" }, Monitor, packages,
                    () => { Ends++; return PendingEnd?.Task ?? Task.CompletedTask; });
                if (catalog != null) typeof(OnlineSessionContext).GetProperty("TrainingCatalog").SetValue(Context, catalog);
                Form = new MainForm(new AppSession(AppMode.Training, "vi", Context), packages,
                    new TrainingWorkspaceService(Path.Combine(folder.Folder, "Working"), Path.Combine(folder.Folder, "Documents"), Path.Combine(folder.Folder, "Pictures")), Word,
                    confirmRestart: message => { Confirms++; Confirmation = message; return Accept; }, notify: _ => { });
                Form.OnlineElapsed = () => Clock.Now; Form.Show(); Application.DoEvents();
            }
            internal void Refresh() { typeof(MainForm).GetMethod("RefreshOnlineTraining", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Form, null); Application.DoEvents(); }
            public void Dispose() { Form.Dispose(); Context.Dispose(); folder.Dispose(); }
        }
        private sealed class OnlineClock : ITestingClock
        {
            internal TimeSpan Now;
            public TimeSpan Monotonic => Now;
            public DateTime UtcNow => throw new AssertFailedException("Training authorization must not use wall time.");
        }
        private sealed class OnlineTransport : IHeartbeatTransport
        {
            private readonly Guid id; private readonly OnlineClock clock;
            private readonly DateTimeOffset start = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            internal bool Reject;
            internal OnlineTransport(Guid id, OnlineClock clock) { this.id = id; this.clock = clock; }
            public Task<HeartbeatReply> SendAsync(Guid sessionId, string token, long? elapsed, bool offline, CancellationToken ct)
            {
                if (Reject) throw new HeartbeatSessionRejectedException(401);
                return Task.FromResult(new HeartbeatReply { SessionId = id, Mode = "Training", SessionToken = "renewed", ServerTime = start + clock.Now,
                    ExpiresAt = start.AddHours(4), RemainingSeconds = (TimeSpan.FromHours(4) - clock.Now).TotalSeconds });
            }
        }
        private sealed class OnlineWord : IWordController, IWordInputLock
        {
            internal bool InputEnabled = true;
            internal int Saves;
            public bool IsOpened => false;
            public void SetInputEnabled(bool enabled) { InputEnabled = enabled; }
            public void StartWord() { }
            public void OpenDocument(string path) { }
            public void Save() { Saves++; }
            public void CloseDocument() { }
            public void ReleaseClosedApplication() { }
            public void Close() { }
            public void Dispose() { }
        }

        [TestMethod]
        public void GoJoinsWarmupWhileUiPaintsAndBarsAreGated()
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                using (var gate = new ManualResetEventSlim())
                using (var host = new TrainingWordController(() => new FakeWord { StartGate = gate }, w => ((FakeWord)w).Alive))
                {
                    var fake = TrainingWordController.Wait(host.InvokeAsync(w => (FakeWord)w));
                    string empty = Path.Combine(folder.Folder, "Packages"); Directory.CreateDirectory(empty);
                    var workspace = new TrainingWorkspaceService(Path.Combine(folder.Folder, "Working"), Path.Combine(folder.Folder, "Documents"), Path.Combine(folder.Folder, "Pictures"));
                    using (var form = new MainForm(new AppSession(AppMode.Training, "vi"), empty, workspace, host, notify: _ => { }))
                    {
                        var selector = (ComboBox)form.Controls.Find("Projects", true).Single();
                        selector.Items.Add(new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "vi").Single(p => p.Meta.ProjectId == "Word2019_P10")); selector.SelectedIndex = 0;
                        form.Show(); Application.DoEvents(); Assert.IsTrue(selector.Enabled); Assert.AreEqual(WordWarmupState.Warming, host.Warmup.State);
                        int pulses = 0; bool busyPainted = false;
                        using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
                        {
                            timer.Tick += (s, e) =>
                            {
                                pulses++; busyPainted = form.UseWaitCursor && form.Controls.Find("OpeningProject", true).Single().Visible;
                                if (pulses == 3) { timer.Stop(); gate.Set(); }
                            };
                            timer.Start(); ((Button)form.Controls.Find("Go", true).Single()).PerformClick();
                        }
                        Assert.IsTrue(pulses >= 3 && busyPainted); Assert.AreEqual(1, fake.Activations); Assert.AreEqual(1, fake.Opens);
                        Assert.AreEqual(WordWarmupState.Ready, host.Warmup.State); Assert.IsTrue(selector.Enabled); Assert.IsFalse(form.UseWaitCursor);
                        Assert.AreEqual(17, ((TabControl)form.Controls.Find("tabTasks", true).Single()).TabCount);
                        StringAssert.Contains(form.LastGoTiming, "EndTrainingSessionMs="); StringAssert.Contains(form.LastGoTiming, "BuildTabsMs=");
                    }
                }
            });
        }
        [TestMethod]
        public void HostUsesOneStaForWarmOpenSaveCallbacksAndRewarm()
        {
            Sta(() =>
            {
                int ui = Thread.CurrentThread.ManagedThreadId, callbackThread = 0;
                using (var host = new TrainingWordController(() => new FakeWord(), w => ((FakeWord)w).Alive))
                {
                    var fake = TrainingWordController.Wait(host.InvokeAsync(w => (FakeWord)w));
                    host.OwnedDocumentSaving += _ => callbackThread = Thread.CurrentThread.ManagedThreadId;
                    TrainingWordController.Wait(host.Warmup.EnsureAsync().ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));
                    host.OpenDocument("work"); host.Save(); Assert.AreEqual(ui, callbackThread);
                    host.CloseDocument(); host.OpenDocument("next"); Assert.AreEqual(1, fake.Activations); Assert.AreEqual(0, fake.Quits);
                    fake.Alive = false; fake.Opened = false;
                    TrainingWordController.Wait(host.Warmup.CheckAsync().ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));
                    Assert.AreEqual(2, fake.Activations); Assert.AreEqual(1, fake.Retires);
                    Assert.AreNotEqual(ui, fake.Owner); Assert.AreEqual(ApartmentState.STA, fake.Apartment);
                }
            });
        }
        [TestMethod]
        public void FailedWarmupFallsBackOnSameControllerAndBecomesReadyAfterOpen()
        {
            Sta(() =>
            {
                using (var host = new TrainingWordController(() => new FakeWord { FailFirst = true }, w => ((FakeWord)w).Alive))
                {
                    var fake = TrainingWordController.Wait(host.InvokeAsync(w => (FakeWord)w));
                    host.JoinWarmup(); Assert.AreEqual(WordWarmupState.Failed, host.Warmup.State);
                    host.OpenDocument("work"); Assert.AreEqual(WordWarmupState.Ready, host.Warmup.State); Assert.AreEqual(1, fake.Activations);
                }
            });
        }
        [TestMethod]
        public void ClosingDuringWarmupDefersCleanupUntilActivationFinishes()
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                using (var gate = new ManualResetEventSlim())
                using (var host = new TrainingWordController(() => new FakeWord { StartGate = gate }, w => ((FakeWord)w).Alive))
                {
                    var fake = TrainingWordController.Wait(host.InvokeAsync(w => (FakeWord)w));
                    string empty = Path.Combine(folder.Folder, "Packages"); Directory.CreateDirectory(empty);
                    using (var form = new MainForm(new AppSession(AppMode.Training, "en"), empty,
                        new TrainingWorkspaceService(Path.Combine(folder.Folder, "Working"), Path.Combine(folder.Folder, "Documents"), Path.Combine(folder.Folder, "Pictures")), host, notify: _ => { }))
                    {
                        form.Show(); Application.DoEvents(); Assert.AreEqual(WordWarmupState.Warming, host.Warmup.State);
                        form.Close(); Assert.IsFalse(form.IsDisposed); Assert.AreEqual(0, fake.Quits);
                        gate.Set();
                        var wait = System.Diagnostics.Stopwatch.StartNew();
                        while (!form.IsDisposed && wait.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
                        Assert.IsTrue(form.IsDisposed); Assert.AreEqual(1, fake.Quits);
                    }
                }
            });
        }
        private static void Sta(Action test)
        {
            Exception failure = null; var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.IsTrue(thread.Join(20000), "Training STA test timed out.");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private sealed class FakeWord : IWordController, IWordSaveEvents, IWordDocumentState, IWordWindowLayout, IWordInputLock
        {
            internal readonly int Owner = Thread.CurrentThread.ManagedThreadId;
            internal readonly ApartmentState Apartment = Thread.CurrentThread.GetApartmentState();
            internal volatile bool Alive, Opened;
            internal bool FailFirst, FailOpen;
            internal int Activations, Opens, Quits, Retires;
            internal bool InputEnabled = true;
            public void SetInputEnabled(bool enabled) { OwnerOnly(); InputEnabled = enabled; }
            internal ManualResetEventSlim StartGate;
            private string path;
            public string CurrentDocumentPath { get { OwnerOnly(); return path; } }
            public string DefaultTemplateFolder => Path.GetTempPath();
            public void SetWindowBounds(int left, int top, int width, int height) { OwnerOnly(); }
            public event Action<string> OwnedDocumentSaving;
            public event Action<string> WorkingFileWritten;
            private void OwnerOnly() { Assert.AreEqual(Owner, Thread.CurrentThread.ManagedThreadId); }
            public bool IsOpened { get { OwnerOnly(); return Opened; } }
            public void StartWord()
            {
                OwnerOnly(); if (Alive) return;
                if (FailFirst) { FailFirst = false; throw new InvalidOperationException("activation failed"); }
                if (StartGate != null && !StartGate.Wait(10000)) throw new TimeoutException("test warm gate");
                Alive = true; Activations++;
            }
            public void OpenDocument(string path) { OwnerOnly(); StartWord(); Opens++; if (FailOpen) throw new IOException("Test open failure."); this.path = path; Opened = true; }
            public void Save() { OwnerOnly(); OwnedDocumentSaving?.Invoke("work"); WorkingFileWritten?.Invoke("work"); }
            public void CloseDocument() { OwnerOnly(); Opened = false; }
            public void ReleaseClosedApplication() { OwnerOnly(); Retires++; Alive = false; }
            public void Close() { OwnerOnly(); Opened = Alive = false; Quits++; }
            public void Dispose() { OwnerOnly(); Close(); }
        }
    }
}
