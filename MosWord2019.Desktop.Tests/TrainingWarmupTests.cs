using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
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
        private sealed class FakeWord : IWordController, IWordSaveEvents, IWordDocumentState, IWordWindowLayout
        {
            internal readonly int Owner = Thread.CurrentThread.ManagedThreadId;
            internal readonly ApartmentState Apartment = Thread.CurrentThread.GetApartmentState();
            internal volatile bool Alive, Opened;
            internal bool FailFirst;
            internal int Activations, Opens, Quits, Retires;
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
            public void OpenDocument(string path) { OwnerOnly(); StartWord(); this.path = path; Opened = true; Opens++; }
            public void Save() { OwnerOnly(); OwnedDocumentSaving?.Invoke("work"); WorkingFileWritten?.Invoke("work"); }
            public void CloseDocument() { OwnerOnly(); Opened = false; }
            public void ReleaseClosedApplication() { OwnerOnly(); Retires++; Alive = false; }
            public void Close() { OwnerOnly(); Opened = Alive = false; Quits++; }
            public void Dispose() { OwnerOnly(); Close(); }
        }
    }
}
