using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Projects;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Threading;

namespace MosWord2019.Tests
{
    [TestClass]
    public class SessionRecoveryJournalTests
    {
        [DataTestMethod]
        [DataRow("asset")]
        [DataRow("output")]
        [DataRow("working")]
        public void CrashReloadDeletesOnlyTrackedUnchangedFile(string kind)
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                string tracked = Write(workspace, "owned.dat", "session");
                string personal = Write(workspace, "personal.dat", "session");
                journal.Track(tracked, kind);
                Assert.AreEqual(1, journal.Count);
                var reloaded = New(journalPath);
                Assert.AreNotEqual(journal.SessionId, reloaded.SessionId);
                Assert.AreEqual(1, reloaded.Count);
                reloaded.Recover();
                Assert.IsFalse(File.Exists(tracked));
                Assert.IsTrue(File.Exists(personal));
                Assert.AreEqual(0, New(journalPath).Count);
            }
        }

        [TestMethod]
        public void ModifiedFileSurvivesAndItsClaimIsDropped()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                string path = Write(workspace, "owned.dat", "first");
                journal.Track(path, "output");
                File.WriteAllText(path, "personal edit");
                var messages = new List<string>();
                new SessionRecoveryJournal(journalPath, messages.Add).Recover();
                Assert.AreEqual("personal edit", File.ReadAllText(path));
                Assert.AreEqual(0, New(journalPath).Count);
                Assert.IsTrue(messages.Any(m => m.Contains("changed/replaced")));
            }
        }

        [TestMethod]
        public void ReplacementWithIdenticalBytesSurvives()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                string path = Write(workspace, "owned.dat", "same bytes");
                journal.Track(path, "asset");
                // Keep the original alive so its file identifier cannot be recycled.
                File.Move(path, path + ".original");
                File.WriteAllText(path, "same bytes");
                New(journalPath).Recover();
                Assert.IsTrue(File.Exists(path));
                Assert.IsTrue(File.Exists(path + ".original"));
                Assert.AreEqual(0, New(journalPath).Count);
            }
        }

        [TestMethod]
        public void LockedClaimPersistsAcrossReloadAndRetriesAfterUnlock()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                string path = Write(workspace, "owned.dat", "session");
                journal.Track(path, "working");
                using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    New(journalPath).Recover();
                    Assert.AreEqual(1, New(journalPath).Count);
                    Assert.IsTrue(File.Exists(path));
                }
                New(journalPath).Recover();
                Assert.IsFalse(File.Exists(path));
                Assert.AreEqual(0, New(journalPath).Count);
            }
        }

        [TestMethod]
        public void AbsentClaimIsClearedWithoutCreatingAnything()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                string path = Write(workspace, "owned.dat", "session");
                journal.Track(path, "asset");
                File.Delete(path);
                New(journalPath).Recover();
                Assert.AreEqual(0, New(journalPath).Count);
                Assert.IsFalse(File.Exists(path));
            }
        }

        [TestMethod]
        public void ConditionalForgetAndAtomicRewritePreserveOtherClaims()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                string first = Write(workspace, "first.dat", "one");
                string second = Write(workspace, "second.dat", "two");
                journal.Track(first, "asset");
                var firstClaim = (JObject)JObject.Parse(File.ReadAllText(journalPath))["entries"][0];
                journal.Track(second, "output");
                journal.Forget(first, "0:0:0");
                Assert.AreEqual(2, New(journalPath).Count);
                journal.Forget(first, (string)firstClaim["identity"]);
                Assert.AreEqual(1, New(journalPath).Count);
                Assert.AreEqual(0, Directory.GetFiles(workspace.Folder, "*.tmp").Length);
                New(journalPath).Recover();
                Assert.IsTrue(File.Exists(first));
                Assert.IsFalse(File.Exists(second));
                Assert.AreEqual(0, New(journalPath).Count);
            }
        }

        [TestMethod]
        public void VerifiedFactsOverloadRoundTripsWithoutReopeningLockedAsset()
        {
            using (var workspace = new TestWorkspace())
            {
                string firstJournal = JournalPath(workspace);
                string path = Write(workspace, "owned.dat", "asset");
                New(firstJournal).Track(path, "asset");
                var claim = JObject.Parse(File.ReadAllText(firstJournal))["entries"][0];
                string nextJournal = Path.Combine(workspace.Folder, "next.json");
                var journal = New(nextJournal);
                using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    journal.Track(path, (string)claim["identity"], (string)claim["sha256"], "asset", (string)claim["sessionId"]);
                New(nextJournal).Recover();
                Assert.IsFalse(File.Exists(path));
            }
        }

        [DataTestMethod]
        [DataRow("syntax")]
        [DataRow("missingHash")]
        [DataRow("duplicatePath")]
        [DataRow("duplicateProperty")]
        [DataRow("relative")]
        [DataRow("invalidKind")]
        [DataRow("invalidIdentity")]
        [DataRow("invalidHash")]
        [DataRow("emptySession")]
        [DataRow("extraProperty")]
        public void MalformedOrAmbiguousJournalNeverDeletesOrOverwrites(string defect)
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                string path = Write(workspace, "owned.dat", "session");
                New(journalPath).Track(path, "output");
                var root = JObject.Parse(File.ReadAllText(journalPath));
                var array = (JArray)root["entries"];
                var claim = (JObject)array[0];
                switch (defect)
                {
                    case "missingHash": claim.Remove("sha256"); break;
                    case "duplicatePath": array.Add(claim.DeepClone()); break;
                    case "relative": claim["path"] = "owned.dat"; break;
                    case "invalidKind": claim["kind"] = "directory"; break;
                    case "invalidIdentity": claim["identity"] = "4294967296:1:1"; break;
                    case "invalidHash": claim["sha256"] = "same name"; break;
                    case "emptySession": claim["sessionId"] = " "; break;
                    case "extraProperty": claim["recursive"] = true; break;
                }
                string malformed = defect == "syntax" ? "{ broken" : root.ToString();
                if (defect == "duplicateProperty") malformed = malformed.Replace("\"version\": 1", "\"version\": 1, \"version\": 1");
                File.WriteAllText(journalPath, malformed);
                var journal = New(journalPath);
                journal.Recover();
                Assert.IsTrue(File.Exists(path));
                Assert.AreEqual(malformed, File.ReadAllText(journalPath));
                Assert.ThrowsException<InvalidDataException>(() => journal.Track(path, "asset"));
                Assert.AreEqual(malformed, File.ReadAllText(journalPath));
            }
        }

        [TestMethod]
        public void TrackNeverRefreshesFirstClaimAfterUserEdits()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                string path = Write(workspace, "owned.dat", "first");
                journal.Track(path, "output");
                string before = File.ReadAllText(journalPath);
                File.WriteAllText(path, "personal");
                Assert.ThrowsException<InvalidOperationException>(() => journal.Track(path, "output"));
                Assert.AreEqual(before, File.ReadAllText(journalPath));
                New(journalPath).Recover();
                Assert.AreEqual("personal", File.ReadAllText(path));
            }
        }

        [TestMethod]
        public void InvalidTrackAndEmptyConstructionHaveNoDiskSideEffects()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = Path.Combine(workspace.Folder, "absent", "journal.json");
                var journal = New(journalPath);
                Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(journalPath)));
                Assert.ThrowsException<InvalidDataException>(() => journal.Track(Path.Combine(workspace.Folder, "x"), "1:2:3", new string('A', 64), "other"));
                Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(journalPath)));
                Assert.ThrowsException<InvalidDataException>(() => journal.Track("relative.dat", "1:2:3", new string('A', 64), "output"));
                Assert.ThrowsException<InvalidDataException>(() => journal.Track(journalPath, "1:2:3", new string('A', 64), "output"));
            }
        }

        [TestMethod]
        public void FailedAtomicRewriteKeepsPriorJournalAndRollsBackNewClaim()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                string first = Write(workspace, "first.dat", "one");
                string second = Write(workspace, "second.dat", "two");
                journal.Track(first, "output");
                string before = File.ReadAllText(journalPath);
                using (var held = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.ThrowsException<IOException>(() => journal.Track(second, "asset"));
                Assert.AreEqual(before, File.ReadAllText(journalPath));
                Assert.AreEqual(1, journal.Count);
                Assert.AreEqual(1, New(journalPath).Count);
                Assert.AreEqual(0, Directory.GetFiles(workspace.Folder, "*.tmp").Length);
                New(journalPath).Recover();
                Assert.IsFalse(File.Exists(first));
                Assert.IsTrue(File.Exists(second));
            }
        }

        [TestMethod]
        public void AtomicRewriteRetriesTransientSharingFailureAfterReaderReleases()
        {
            using (var workspace = new TestWorkspace())
            using (var releaseReader = new ManualResetEventSlim())
            using (var readerReleased = new ManualResetEventSlim())
            {
                string journalPath = JournalPath(workspace);
                string first = Write(workspace, "first.dat", "one");
                string second = Write(workspace, "second.dat", "two");
                int retries = 0;
                var journal = new SessionRecoveryJournal(journalPath, message =>
                {
                    if (!message.StartsWith("Recovery journal atomic replace retry ", StringComparison.Ordinal)) return;
                    Interlocked.Increment(ref retries);
                    releaseReader.Set();
                    Assert.IsTrue(readerReleased.Wait(TimeSpan.FromSeconds(5)), "Reader must release before the next retry.");
                });
                journal.Track(first, "output");
                var held = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var releaseTask = Task.Run(() =>
                {
                    try { Assert.IsTrue(releaseReader.Wait(TimeSpan.FromSeconds(5)), "Expected a retry before releasing the reader."); }
                    finally { held.Dispose(); readerReleased.Set(); }
                });
                try { journal.Track(second, "asset"); }
                finally { releaseReader.Set(); releaseTask.GetAwaiter().GetResult(); held.Dispose(); }
                Assert.IsTrue(retries >= 1 && retries <= 3);
                Assert.AreEqual(2, New(journalPath).Count);
                Assert.AreEqual(0, Directory.GetFiles(workspace.Folder, "*.tmp").Length);
                New(journalPath).Recover();
                Assert.IsFalse(File.Exists(first));
                Assert.IsFalse(File.Exists(second));
            }
        }

        [TestMethod]
        public void ReadOnlyFileIsRetainedUntilRetryIsSafe()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                string path = Write(workspace, "owned.dat", "session");
                New(journalPath).Track(path, "working");
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try
                {
                    New(journalPath).Recover();
                    Assert.IsTrue(File.Exists(path));
                    Assert.AreEqual(1, New(journalPath).Count);
                }
                finally { File.SetAttributes(path, FileAttributes.Normal); }
                New(journalPath).Recover();
                Assert.IsFalse(File.Exists(path));
                Assert.AreEqual(0, New(journalPath).Count);
            }
        }

        [TestMethod]
        public void RefreshWorkingPersistsVerifiedSaveWithoutForgettingOwnership()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                string path = Write(workspace, "working.docx", "starter copy");
                var journal = New(journalPath);
                journal.Track(path, "working");
                string identity = Identity(journalPath);
                File.WriteAllText(path, "verified owned save");
                journal.RefreshWorking(path, identity, Hash(path));
                Assert.AreEqual(1, journal.Count);
                New(journalPath).Recover();
                Assert.IsFalse(File.Exists(path));
                Assert.AreEqual(0, New(journalPath).Count);
            }
        }

        [TestMethod]
        public void FailedWorkingRefreshRetainsOriginalClaimOnDiskAndInMemory()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                string path = Write(workspace, "working.docx", "starter copy");
                var journal = New(journalPath);
                journal.Track(path, "working");
                string identity = Identity(journalPath);
                string before = File.ReadAllText(journalPath);
                File.WriteAllText(path, "verified owned save");
                using (var held = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.ThrowsException<IOException>(() => journal.RefreshWorking(path, identity, Hash(path)));
                Assert.AreEqual(before, File.ReadAllText(journalPath));
                Assert.AreEqual(1, journal.Count);
                // Current instance still has the old hash; it must preserve these newer bytes.
                journal.Recover();
                Assert.AreEqual("verified owned save", File.ReadAllText(path));
                Assert.AreEqual(0, New(journalPath).Count);
            }
        }

        [DataTestMethod]
        [DataRow("asset", false)]
        [DataRow("output", false)]
        [DataRow("working", true)]
        public void WorkingRefreshRejectsOtherKindsAndEarlierSessions(string kind, bool reload)
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                string path = Write(workspace, "owned.dat", "session");
                var journal = New(journalPath);
                journal.Track(path, kind);
                string before = File.ReadAllText(journalPath);
                if (reload) journal = New(journalPath);
                Assert.ThrowsException<InvalidOperationException>(() => journal.RefreshWorking(path, Identity(journalPath), Hash(path)));
                Assert.AreEqual(before, File.ReadAllText(journalPath));
            }
        }

        [TestMethod]
        public void WorkingRefreshCanCreateNewClaimFromVerifiedFacts()
        {
            using (var workspace = new TestWorkspace())
            {
                string path = Write(workspace, "working.docx", "session");
                string factsJournal = Path.Combine(workspace.Folder, "facts.json");
                New(factsJournal).Track(path, "working");
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                journal.RefreshWorking(path, Identity(factsJournal), Hash(path));
                Assert.AreEqual(1, New(journalPath).Count);
                New(journalPath).Recover();
                Assert.IsFalse(File.Exists(path));
            }
        }

        [TestMethod]
        public void ConcurrentClaimsAndRecoveryRetainCompleteDurableState()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = JournalPath(workspace);
                var journal = New(journalPath);
                var paths = Enumerable.Range(0, 8).Select(i => Write(workspace, i + ".dat", "session " + i)).ToArray();
                Parallel.ForEach(paths, path => journal.Track(path, "asset"));
                Assert.AreEqual(paths.Length, journal.Count);
                Assert.AreEqual(paths.Length, New(journalPath).Count);
                Parallel.Invoke(journal.Recover, journal.Recover, () =>
                {
                    for (int i = 0; i < 20; i++)
                    {
                        Assert.IsTrue(journal.Count >= 0);
                        journal.Forget(Path.Combine(workspace.Folder, "absent.dat"));
                    }
                });
                Assert.IsTrue(paths.All(path => !File.Exists(path)));
                Assert.AreEqual(0, New(journalPath).Count);
            }
        }

        private static string Identity(string journalPath) => (string)JObject.Parse(File.ReadAllText(journalPath))["entries"][0]["identity"];
        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static SessionRecoveryJournal New(string path) => new SessionRecoveryJournal(path, _ => { });
        private static string JournalPath(TestWorkspace workspace) => Path.Combine(workspace.Folder, "journal.json");
        private static string Write(TestWorkspace workspace, string name, string contents)
        {
            string path = Path.Combine(workspace.Folder, name);
            File.WriteAllText(path, contents);
            return path;
        }
    }
}
