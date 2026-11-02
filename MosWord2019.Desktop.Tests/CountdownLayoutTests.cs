using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using MosWord2019.Projects.OnlineSessions;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class CountdownLayoutTests
    {
        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void TrainingSixtySecondTicksDoNotLayoutHeaderOrFooter(string language)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var clock = new Clock();
                    Guid id = Guid.NewGuid();
                    using (var monitor = new SessionConnectivityMonitor(id, "Training", "token", new Transport(id, clock), clock))
                    {
                        string packages = Path.Combine(folder.Folder, "Packages");
                        Directory.CreateDirectory(packages);
                        using (var context = new OnlineSessionContext(new OnlineSessionCredentials { SessionId = id, Mode = "Training", Language = language }, monitor, packages, () => Task.CompletedTask))
                        {
                            // An empty catalog keeps this clock regression independent of Word activation/downloads.
                            typeof(OnlineSessionContext).GetProperty("TrainingCatalog").SetValue(context, new ProjectPackage[0]);
                            using (var form = new MainForm(new AppSession(AppMode.Training, language, context), packages,
                                new TrainingWorkspaceService(Path.Combine(folder.Folder, "Working"), Path.Combine(folder.Folder, "Documents"), Path.Combine(folder.Folder, "Pictures")),
                                new Word(), notify: _ => { }))
                            {
                                form.OnlineElapsed = () => clock.Now;
                                monitor.PollAsync().GetAwaiter().GetResult();
                                form.Show();
                                Action refresh = () => typeof(MainForm).GetMethod("RefreshOnlineTraining", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                                refresh(); Application.DoEvents();
                                Assert.IsTrue(monitor.IsOnline);
                                VerifyTicks(form, "pnlTopBar", "pnlFooter", "TrainingCountdown", second =>
                                {
                                    clock.Now = TimeSpan.FromSeconds(second);
                                    refresh();
                                });
                            }
                        }
                    }
                }
            });
        }

        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void TestingSixtySecondTicksDoNotLayoutHeaderOrFooter(string language)
        {
            Sta(() =>
            {
                using (var folder = new TestWorkspace())
                {
                    var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                    var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json"))
                        .Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                    using (var form = new TestingForm(prepared, packages))
                    {
                        Func<int, TestingSessionSnapshot> state = second => new TestingSessionSnapshot(TestingSessionStatus.Running,
                            TestingFinalizeReason.None, 0, TimeSpan.FromSeconds(second), Enumerable.Repeat(TestingProjectState.Active, 7), new ExamTaskGrade[0], null);
                        form.Show(); form.ApplyState(state(0)); Application.DoEvents();
                        VerifyTicks(form, "ExamTopBar", "ExamFooter", "Countdown", second => form.ApplyState(state(second)));
                    }
                }
            });
        }

        private static void VerifyTicks(Control form, string headerName, string footerName, string countdownName, Action<int> tick)
        {
            Control header = form.Controls.Find(headerName, true).Single();
            Control footer = form.Controls.Find(footerName, true).Single();
            Control[] footerControls = Tree(footer).ToArray();
            var bounds = footerControls.Select(c => c.Bounds).ToArray();
            var texts = footerControls.Select(c => c.Text).ToArray();
            var containers = Tree(header).Concat(Tree(footer)).Where(c => c is Panel || c.HasChildren).ToArray();
            var layouts = new List<string>();
            foreach (var container in containers)
                container.Layout += (sender, args) => layouts.Add(container.Name + "/" + container.GetType().Name + ":" + args.AffectedProperty);
            int changes = 0;
            form.Controls.Find(countdownName, true).Single().TextChanged += (sender, args) => changes++;
            for (int second = 1; second <= 60; second++)
            {
                tick(second); Application.DoEvents();
                CollectionAssert.AreEqual(bounds, footerControls.Select(c => c.Bounds).ToArray(), "Footer bounds changed at second " + second);
                CollectionAssert.AreEqual(texts, footerControls.Select(c => c.Text).ToArray(), "Footer text changed at second " + second);
            }
            Assert.AreEqual(60, changes, "The production refresh path must update the countdown once per second.");
            Assert.AreEqual(0, layouts.Count, "Clock ticks must not reflow header/footer containers, including nested containers: " + string.Join(", ", layouts.Take(8)));
        }

        private static IEnumerable<Control> Tree(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls)
                foreach (Control nested in Tree(child)) yield return nested;
        }

        private static void Sta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(20000), "Countdown layout test timed out.");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed class Clock : ITestingClock
        {
            internal TimeSpan Now;
            public TimeSpan Monotonic => Now;
            public DateTime UtcNow => throw new AssertFailedException("Countdown authorization must use monotonic/server time.");
        }

        private sealed class Transport : IHeartbeatTransport
        {
            private readonly Guid id;
            private readonly Clock clock;
            private readonly DateTimeOffset start = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            internal Transport(Guid id, Clock clock) { this.id = id; this.clock = clock; }
            public Task<HeartbeatReply> SendAsync(Guid sessionId, string token, long? elapsed, bool offline, CancellationToken ct) =>
                Task.FromResult(new HeartbeatReply { SessionId = id, Mode = "Training", SessionToken = "renewed", ServerTime = start + clock.Now,
                    ExpiresAt = start.AddHours(4), RemainingSeconds = (TimeSpan.FromHours(4) - clock.Now).TotalSeconds });
        }

        private sealed class Word : IWordController, IWordInputLock
        {
            public bool IsOpened => false;
            public void SetInputEnabled(bool enabled) { }
            public void StartWord() { Assert.Fail("Countdown ticks must not activate Word."); }
            public void OpenDocument(string path) { Assert.Fail("Countdown ticks must not open a document."); }
            public void Save() { }
            public void CloseDocument() { }
            public void ReleaseClosedApplication() { }
            public void Close() { }
            public void Dispose() { }
        }
    }
}
