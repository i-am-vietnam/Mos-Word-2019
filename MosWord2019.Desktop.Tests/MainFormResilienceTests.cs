using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MosWord2019.Tests
{
    [TestClass]
    public class MainFormResilienceTests
    {
        [TestMethod]
        public void CleanupRetryIncludesReasonAndEventuallyCloses()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                fake.DisposeFailures = 1;
                var e = new FormClosingEventArgs(CloseReason.UserClosing, false);
                Call(form, "OnFormClosing", e);
                Assert.IsFalse(e.Cancel);
                Assert.AreEqual(2, fake.DisposeCalls);
                Assert.AreEqual(1, messages.Count);
                StringAssert.Contains(messages[0], "fake cleanup reason");
            }, retry: true));
        }

        [TestMethod]
        public void CloseAnywaySkipsAllLaterControllerCleanupAndKeepsJournal()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                fake.DisposeFailures = int.MaxValue;
                var e = new FormClosingEventArgs(CloseReason.UserClosing, false);
                Call(form, "OnFormClosing", e);
                Assert.IsFalse(e.Cancel);
                form.Dispose();
                Call(form, "CloseOwnedForRecovery");
                Assert.AreEqual(1, fake.DisposeCalls);
                Assert.AreEqual(1, journal.Count);
                Assert.AreEqual(1, messages.Count);
            }));
        }

        [TestMethod]
        public void WindowsShutdownMakesNoControllerCallsAndKeepsJournal()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                var e = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
                Call(form, "OnFormClosing", e);
                form.Dispose();
                Assert.IsFalse(e.Cancel);
                Assert.AreEqual(0, fake.TotalCalls);
                Assert.AreEqual(0, messages.Count);
                Assert.AreEqual(1, journal.Count);
            }));
        }

        [TestMethod]
        public void TaskManagerCloseDefersAllControllerCallsUntilUiDispatch()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                var handle = form.Handle;
                var e = new FormClosingEventArgs(CloseReason.TaskManagerClosing, false);
                Call(form, "OnFormClosing", e);
                Assert.IsTrue(e.Cancel);
                Assert.AreEqual(0, fake.TotalCalls);
                Application.DoEvents();
                Assert.AreEqual(1, fake.DisposeCalls);
                Assert.AreEqual(0, messages.Count);
            }));
        }

        [TestMethod]
        public void NestedCalloutFailureRetriesAfterDispatchOnlyOnce()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                var handle = form.Handle;
                fake.DisposeFailures = 1;
                fake.CleanupError = new AggregateException(new InvalidOperationException("wrapped",
                    new COMException("callout", unchecked((int)0x8001010D))));
                var e = new FormClosingEventArgs(CloseReason.UserClosing, false);
                Call(form, "OnFormClosing", e);
                Assert.IsTrue(e.Cancel);
                Assert.AreEqual(1, fake.DisposeCalls);
                Assert.AreEqual(0, messages.Count);
                Application.DoEvents();
                Assert.AreEqual(2, fake.DisposeCalls);
                Assert.AreEqual(0, messages.Count);
            }));
        }

        [TestMethod]
        public void RepeatedCalloutFailureOffersCloseAnywayWithoutDispatchLoop()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                var handle = form.Handle;
                fake.DisposeFailures = int.MaxValue;
                fake.CleanupError = new COMException("callout", unchecked((int)0x8001010D));
                Call(form, "OnFormClosing", new FormClosingEventArgs(CloseReason.UserClosing, false));
                Application.DoEvents();
                Application.DoEvents();
                Assert.AreEqual(2, fake.DisposeCalls);
                Assert.AreEqual(1, messages.Count);
                Assert.AreEqual(1, journal.Count);
            }));
        }

        [TestMethod]
        public void DisposeFailureNeverEscapesOrRetriesController()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                fake.DisposeFailures = int.MaxValue;
                form.Dispose();
                form.Dispose();
                Assert.AreEqual(1, fake.DisposeCalls);
                Assert.AreEqual(1, journal.Count);
                Assert.AreEqual(0, messages.Count);
            }));
        }

        [TestMethod]
        public void BaseDisposeEventFailureNeverEscapes()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                form.Disposed += (sender, args) => { throw new InvalidOperationException("base disposed subscriber failed"); };
                form.Dispose();
                Assert.AreEqual(1, fake.DisposeCalls);
                Assert.IsTrue((bool)Get(form, "skipCleanup"));
                Assert.AreEqual(1, journal.Count);
            }));
        }

        [DataTestMethod]
        [DataRow("en", "Please close any open dialog in Word and try again.")]
        [DataRow("vi", "Hãy đóng hộp thoại đang mở trong Word rồi thử lại.")]
        public void NestedBusyRunUsesFriendlyMessage(string language, string expected)
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                Action operation = () => { throw new AggregateException(new COMException("busy", unchecked((int)0x8001010A))); };
                Call(form, "Run", operation);
                Assert.AreEqual(expected, messages[0]);
            }, language: language));
        }

        [TestMethod]
        public void OnlyMissingTranslationUsesTranslationMessage()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                Call(form, "Run", (Action)(() => { throw new InvalidDataException("unrelated data"); }));
                Assert.IsFalse(messages[0].Contains("translations"));
                var package = new ProjectPackage();
                package.Tasks.Add(new TaskDefinition { TaskId = "T01", TitleKey = "title", InstructionKey = "instructions" });
                Exception translation = null;
                try { Call(form, "ValidateTranslations", package); }
                catch (TargetInvocationException ex) { translation = ex.InnerException; }
                Assert.IsNotNull(translation);
                Assert.AreEqual("MissingTranslationException", translation.GetType().Name);
                Call(form, "Run", (Action)(() => { throw translation; }));
                StringAssert.Contains(messages[1], "translations");
            }));
        }

        [TestMethod]
        public void CleanupReadFailurePreservesJournalAndDoesNotClaimSuccess()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                Set(form, "currentProject", new ProjectPackage());
                fake.ReadError = new InvalidOperationException("read state failed");
                var e = new FormClosingEventArgs(CloseReason.UserClosing, false);
                Call(form, "OnFormClosing", e);
                Assert.IsFalse(e.Cancel);
                Assert.AreEqual(0, fake.DisposeCalls);
                Assert.AreEqual(1, journal.Count);
                StringAssert.Contains(messages[0], "read state failed");
                Assert.IsFalse((bool)Get(form, "cleanupCompleted"));
            }));
        }

        [DataTestMethod]
        [DataRow("en", "Please close any open dialog in Word and try again.")]
        [DataRow("vi", "Hãy đóng hộp thoại đang mở trong Word rồi thử lại.")]
        public void BusyDocumentReadDoesNotEndSession(string language, string expected)
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                Set(form, "currentProject", new ProjectPackage());
                fake.ReadError = new InvalidOperationException("wrapped", new COMException("busy", unchecked((int)0x80010001)));
                Assert.AreEqual(false, Call(form, "CheckDocument"));
                Assert.IsNotNull(Get(form, "currentProject"));
                Assert.AreEqual(expected, messages[0]);
                Assert.AreEqual(1, journal.Count);
                fake.ReadError = null;
                Set(form, "currentProject", null);
            }, language: language));
        }

        [DataTestMethod]
        [DataRow("en", "Please close any open dialog in Word and try again.")]
        [DataRow("vi", "Hãy đóng hộp thoại đang mở trong Word rồi thử lại.")]
        public void BusyGradeSaveUsesFriendlyMessage(string language, string expected)
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                var project = new ProjectPackage();
                project.Tasks.Add(new TaskDefinition { TaskId = "T01" });
                Set(form, "currentProject", project);
                Set(form, "taskIndex", 0);
                fake.Opened = true;
                fake.SaveError = new COMException("busy", unchecked((int)0x8001010A));
                Call(form, "GradeCurrentTask");
                Assert.AreEqual(expected, messages[0]);
                Assert.AreEqual(1, journal.Count);
                Set(form, "currentProject", null);
            }, language: language));
        }

        [TestMethod]
        public void SaveNotificationDoesNotQueryControllerAndMonitorStopsAtDeadline()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                var handle = form.Handle;
                Call(form, "OwnedDocumentSaving", Path.Combine(Path.GetTempPath(), "unclaimed.docx"));
                Assert.AreEqual(0, fake.TotalCalls);
                Application.DoEvents();
                var watch = (System.Diagnostics.Stopwatch)Get(form, "saveObservationWindow");
                Assert.IsTrue(watch.IsRunning);
                Call(form, "ObserveAfterSave");
                int calls = fake.TotalCalls;
                Thread.Sleep(5050);
                Call(form, "ObserveAfterSave");
                Assert.AreEqual(calls, fake.TotalCalls);
                Assert.IsFalse(((System.Windows.Forms.Timer)Get(form, "savedOutputMonitor")).Enabled);
                Assert.AreEqual(1, journal.Count);
            }));
        }

        [DataTestMethod]
        [DataRow(false, 1)]
        [DataRow(true, 0)]
        public void FatalCleanupRechecksDeferralAfterOwnedCloseFailure(bool defer, int expectedRecoveries)
        {
            var flag = typeof(Program).GetField("recoveryDeferred", BindingFlags.Static | BindingFlags.NonPublic);
            object original = flag.GetValue(null);
            flag.SetValue(null, 0);
            try
            {
                int working = 0, recoveries = 0, failures = 0;
                Program.RunRecoveryCleanup(() =>
                {
                    if (defer) Program.DeferRecoveryToNextStart();
                    throw new InvalidOperationException("owned close failed");
                }, () => working++, () => recoveries++, error => failures++);
                Assert.AreEqual(0, working);
                Assert.AreEqual(expectedRecoveries, recoveries);
                Assert.AreEqual(1, failures);
            }
            finally { flag.SetValue(null, original); }
        }

        [TestMethod]
        public void BusyPostSaveStopsWithoutRepeatedFailureState()
        {
            Sta(() => WithForm((form, fake, journal, messages) =>
            {
                fake.ReadError = new COMException("busy", unchecked((int)0x80010001));
                Set(form, "currentProject", new ProjectPackage());
                Call(form, "ObserveAfterSave");
                Assert.IsNotNull(Get(form, "lastSaveObservationFailure"));
                Call(form, "StopSaveObservation");
                Assert.IsNull(Get(form, "lastSaveObservationFailure"));
                Call(form, "StopSaveObservation");
                Assert.AreEqual(0, messages.Count);
                Assert.AreEqual(1, journal.Count);
                Set(form, "currentProject", null);
            }));
        }

        private static void WithForm(Action<MainForm, FakeController, SessionRecoveryJournal, List<string>> test,
            bool retry = false, string language = "en")
        {
            using (var temp = new TestWorkspace())
            {
                string projects = Path.Combine(temp.Folder, "Projects");
                Directory.CreateDirectory(projects);
                var journal = new SessionRecoveryJournal(Path.Combine(temp.Folder, "journal.json"), _ => { });
                string owned = Path.Combine(temp.Folder, "owned.dat");
                File.WriteAllText(owned, "preserve");
                journal.Track(owned, "output");
                var workspace = new TrainingWorkspaceService(Path.Combine(temp.Folder, "Working"),
                    Path.Combine(temp.Folder, "Documents"), Path.Combine(temp.Folder, "Pictures"), journal);
                var fake = new FakeController();
                var messages = new List<string>();
                var form = new MainForm(new AppSession(AppMode.Training, language), projects, workspace, fake,
                    notify: messages.Add, retryCleanup: reason => { messages.Add(reason); return retry; });
                var flag = typeof(Program).GetField("recoveryDeferred", BindingFlags.Static | BindingFlags.NonPublic);
                object original = flag.GetValue(null);
                flag.SetValue(null, 0);
                try { test(form, fake, journal, messages); }
                finally { try { form.Dispose(); } finally { flag.SetValue(null, original); } }
            }
        }

        private static object Call(MainForm form, string name, params object[] args)
        {
            return typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic).Invoke(form, args);
        }
        private static object Get(MainForm form, string name) { return typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form); }
        private static void Set(MainForm form, string name, object value) { typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value); }
        private static void Sta(Action test)
        {
            Exception error = null;
            var thread = new Thread(() => { try { test(); } catch (Exception ex) { error = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "STA verification timed out.");
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        private sealed class FakeController : IWordController
        {
            public void ReleaseClosedApplication() { }
            internal int DisposeFailures;
            internal int DisposeCalls;
            internal int TotalCalls;
            internal Exception CleanupError = new InvalidOperationException("fake cleanup reason");
            internal Exception ReadError;
            internal Exception SaveError;
            internal bool Opened;
            public bool IsOpened { get { TotalCalls++; if (ReadError != null) throw ReadError; return Opened; } }
            public void StartWord() { TotalCalls++; }
            public void OpenDocument(string path) { TotalCalls++; }
            public void Save() { TotalCalls++; if (SaveError != null) throw SaveError; }
            public void CloseDocument() { TotalCalls++; }
            public void Close() { TotalCalls++; }
            public void Dispose() { TotalCalls++; DisposeCalls++; if (DisposeCalls <= DisposeFailures) throw CleanupError; }
        }
    }
}
