using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Projects;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class WorkspaceRecoveryTests
    {
        [DataTestMethod]
        [DataRow("Word2019_P04")]
        [DataRow("Word2019_P12")]
        [DataRow("Word2019_P26")]
        public void DefaultPicturesAndAliasAssetsAreJournalledAndRecovered(string projectId)
        {
            using (var workspace = new TestWorkspace())
            {
                var journal = Journal(workspace);
                var service = Service(workspace, journal);
                var package = Load(projectId);
                string[] paths = service.StageProjectAssetsToDocuments(package).ToArray();
                Assert.IsTrue(paths.Length > 0);
                Assert.AreEqual(paths.Length, journal.Count);
                if (projectId == "Word2019_P12") Assert.IsTrue(paths.Any(p => p.Contains("Pictures")));
                if (projectId == "Word2019_P26") Assert.IsTrue(paths.Any(p => Path.GetFileName(p) == "Coffee Sam.jpg"));
                new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Recover();
                Assert.IsTrue(paths.All(p => !File.Exists(p)));
                Assert.AreEqual(0, new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Count);
            }
        }

        [TestMethod]
        public void IdenticalPreexistingAssetNeverAcquiresAJournalClaim()
        {
            using (var workspace = new TestWorkspace())
            {
                var package = Load("Word2019_P12");
                string documents = Path.Combine(workspace.Folder, "Documents");
                Directory.CreateDirectory(documents);
                string path = Path.Combine(documents, "Map.png");
                File.Copy(Path.Combine(package.ProjectFolderPath, "assets", "Map.png"), path);
                var journal = Journal(workspace);
                var paths = Service(workspace, journal).StageProjectAssetsToDocuments(package);
                Assert.AreEqual(paths.Count - 1, journal.Count);
                new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Recover();
                Assert.IsTrue(File.Exists(path));
            }
        }

        [TestMethod]
        public void NormalExitRemovesReservedFilesButPreservesUnrelatedAndLockedFiles()
        {
            using (var workspace = new TestWorkspace())
            {
                var journal = Journal(workspace);
                var service = Service(workspace, journal);
                string path = service.PrepareFreshWorkingCopy(Load("Word2019_P01"));
                string directory = Path.GetDirectoryName(path);
                string unrelated = Path.Combine(directory, "personal.docx");
                string checkpoint = Path.Combine(directory, "export-checkpoint.docx");
                File.WriteAllText(unrelated, "personal");
                File.WriteAllText(checkpoint, "checkpoint");
                File.WriteAllText(path, "owned working edits");
                var messages = new List<string>();
                using (var locked = new FileStream(checkpoint, FileMode.Open, FileAccess.Read, FileShare.Read))
                    service.CleanupWorkingFiles(messages.Add);
                Assert.IsFalse(File.Exists(path));
                Assert.IsTrue(File.Exists(unrelated));
                Assert.IsTrue(File.Exists(checkpoint));
                Assert.IsTrue(messages.Any(m => m.Contains("cleanup failed")));
                service.CleanupWorkingFiles(messages.Add);
                Assert.IsFalse(File.Exists(checkpoint));
                Assert.IsTrue(File.Exists(unrelated));
                Assert.IsTrue(Directory.Exists(service.WorkingRootPath));
            }
        }

        [TestMethod]
        public void EmptyKnownWorkingFoldersAreRemovedAndArbitraryFoldersAreRetained()
        {
            using (var workspace = new TestWorkspace())
            {
                var service = Service(workspace, Journal(workspace));
                service.PrepareFreshWorkingCopy(Load("Word2019_P01"));
                string arbitrary = Path.Combine(service.WorkingRootPath, "PersonalFolder");
                Directory.CreateDirectory(arbitrary);
                File.WriteAllText(Path.Combine(arbitrary, "work.docx"), "personal");
                service.CleanupWorkingFiles();
                Assert.IsFalse(Directory.Exists(Path.Combine(service.WorkingRootPath, "Word2019_P01")));
                Assert.IsTrue(File.Exists(Path.Combine(arbitrary, "work.docx")));
            }
        }

        [TestMethod]
        public void NormalCleanupRefusesAReparseProjectDirectory()
        {
            using (var workspace = new TestWorkspace())
            {
                var service = Service(workspace, Journal(workspace));
                Directory.CreateDirectory(service.WorkingRootPath);
                string personal = Path.Combine(workspace.Folder, "PersonalTarget");
                Directory.CreateDirectory(personal);
                string sentinel = Path.Combine(personal, "work.docx");
                File.WriteAllText(sentinel, "personal junction target");
                string link = Path.Combine(service.WorkingRootPath, "Word2019_P99");
                var start = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + personal + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(start))
                {
                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    Assert.AreEqual(0, process.ExitCode, output);
                }
                try
                {
                    var messages = new List<string>();
                    service.CleanupWorkingFiles(messages.Add);
                    Assert.AreEqual("personal junction target", File.ReadAllText(sentinel));
                    Assert.IsTrue(messages.Any(m => m.Contains("reparse")));
                }
                finally { Directory.Delete(link); } // Remove the junction itself before fixture disposal.
            }
        }

        [TestMethod]
        public void CrashRecoveryPreservesUnobservedWorkingEditsAndNeverSweepsReservedNames()
        {
            using (var workspace = new TestWorkspace())
            {
                var service = Service(workspace, Journal(workspace));
                string path = service.PrepareFreshWorkingCopy(Load("Word2019_P01"));
                File.WriteAllText(path, "changed since the owned observation");
                string untracked = Path.Combine(Path.GetDirectoryName(path), "work.doc");
                File.WriteAllText(untracked, "not journalled");
                new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Recover();
                Assert.IsTrue(File.Exists(path));
                Assert.IsTrue(File.Exists(untracked));
            }
        }

        [TestMethod]
        public void NormalCleanupPreservesAnUnobservedReplacementOfOwnedWorkingFile()
        {
            using (var workspace = new TestWorkspace())
            {
                var service = Service(workspace, Journal(workspace));
                string path = service.PrepareFreshWorkingCopy(Load("Word2019_P01"));
                string replacement = Path.Combine(Path.GetDirectoryName(path), "replacement.tmp");
                File.WriteAllText(replacement, "personal replacement");
                File.Replace(replacement, path, null);
                service.ObserveWorkingFiles();
                service.CleanupWorkingFiles();
                Assert.AreEqual("personal replacement", File.ReadAllText(path));
            }
        }

        [TestMethod]
        public void FreshResetReplacesAReleasedOldSessionWorkingClaimAfterLockedRecovery()
        {
            using (var workspace = new TestWorkspace())
            {
                var package = Load("Word2019_P01");
                string path = Service(workspace, Journal(workspace)).PrepareFreshWorkingCopy(package);
                string personal = Path.Combine(Path.GetDirectoryName(path), "personal-guard.docx");
                File.WriteAllText(personal, "personal guard");
                var nextJournal = Journal(workspace);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    nextJournal.Recover();
                    Assert.AreEqual(1, nextJournal.Count, "Locked recovery must retain the preceding session claim.");
                }
                var nextService = Service(workspace, nextJournal);
                Assert.AreEqual(path, nextService.ResetWorkingCopy(package));
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(package.ProjectFolderPath, package.Meta.Starter)), File.ReadAllBytes(path));
                Assert.AreEqual("personal guard", File.ReadAllText(personal));
                Assert.AreEqual(1, nextJournal.Count);
                new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Recover();
                Assert.IsFalse(File.Exists(path));
                Assert.AreEqual("personal guard", File.ReadAllText(personal));
            }
        }

        [TestMethod]
        public void ConcurrentOwnedCallbackAndTimerObservationsKeepStableWorkingClaim()
        {
            using (var workspace = new TestWorkspace())
            {
                var journal = Journal(workspace);
                var service = Service(workspace, journal);
                string path = service.PrepareFreshWorkingCopy(Load("Word2019_P01"));
                Task.WaitAll(
                    Task.Run(() => { for (int i = 0; i < 40; i++) service.ObserveWorkingFiles(); }),
                    Task.Run(() => { for (int i = 0; i < 40; i++) service.RecordOwnedWorkingFile(path); }));
                Assert.AreEqual(1, journal.Count);
                Assert.IsTrue(File.Exists(path));
                new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Recover();
                Assert.IsFalse(File.Exists(path));
            }
        }

        [TestMethod]
        public void ExactOwnedSavePathCanRefreshAtomicSaveIdentity()
        {
            using (var workspace = new TestWorkspace())
            {
                var journal = Journal(workspace);
                var service = Service(workspace, journal);
                string path = service.PrepareFreshWorkingCopy(Load("Word2019_P01"));
                string replacement = Path.Combine(Path.GetDirectoryName(path), "native-save.tmp");
                File.WriteAllText(replacement, "owned Word atomic save");
                File.Replace(replacement, path, null);
                service.ObserveWorkingFiles(path);
                new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Recover();
                Assert.IsFalse(File.Exists(path));
            }
        }

        [TestMethod]
        public void WorkingObservationReadsOwnedLiveWriterWithoutDeletingIt()
        {
            using (var workspace = new TestWorkspace())
            {
                var journal = Journal(workspace);
                var service = Service(workspace, journal);
                string path = service.PrepareFreshWorkingCopy(Load("Word2019_P01"));
                using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                    file.SetLength(0);
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes("owned saved bytes");
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                    service.ObserveWorkingFiles();
                    Assert.IsTrue(File.Exists(path));
                    Assert.AreEqual(1, journal.Count);
                }
                new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Recover();
                Assert.IsFalse(File.Exists(path), "Owned live save must update the crash-recovery hash.");
            }
        }

        [TestMethod]
        public void OwnedSaveObservationRefreshesWorkingHashAndResetDoesNotConflictWithFirstClaim()
        {
            using (var workspace = new TestWorkspace())
            {
                var journal = Journal(workspace);
                var service = Service(workspace, journal);
                var package = Load("Word2019_P01");
                string path = service.PrepareFreshWorkingCopy(package);
                service.ResetWorkingCopy(package);
                File.WriteAllText(path, "saved by owned Word");
                service.ObserveWorkingFiles();
                Assert.AreEqual(1, journal.Count);
                new SessionRecoveryJournal(JournalPath(workspace), _ => { }).Recover();
                Assert.IsFalse(File.Exists(path));
            }
        }

        private static ProjectPackage Load(string projectId) =>
            new ProjectLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, projectId), "en");
        private static string JournalPath(TestWorkspace workspace) => Path.Combine(workspace.Folder, "journal.json");
        private static SessionRecoveryJournal Journal(TestWorkspace workspace) => new SessionRecoveryJournal(JournalPath(workspace), _ => { });
        private static TrainingWorkspaceService Service(TestWorkspace workspace, SessionRecoveryJournal journal) =>
            new TrainingWorkspaceService(Path.Combine(workspace.Folder, "Working"), Path.Combine(workspace.Folder, "Documents"),
                Path.Combine(workspace.Folder, "Pictures"), journal);
    }
}
