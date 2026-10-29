using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public class OutputOwnershipLedgerTests
    {
        [TestMethod]
        public void BaselineRecordsExistingFactsAndMissingParentsWithoutCreatingThem()
        {
            using (var workspace = new TestWorkspace())
            {
                string path = Path.Combine(workspace.Folder, "answer.txt");
                File.WriteAllText(path, "old answer");
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                var baseline = ledger.Capture().Outputs.Single().Baseline;
                Assert.IsTrue(baseline.Exists);
                Assert.AreEqual(new FileInfo(path).Length, baseline.Length);
                Assert.AreEqual(64, baseline.Sha256.Length);
                Assert.IsFalse(string.IsNullOrWhiteSpace(baseline.Identity));
                Assert.AreEqual(File.GetLastWriteTimeUtc(path), baseline.LastWriteUtc);
                Assert.AreEqual(DateTimeKind.Utc, baseline.ObservedUtc.Kind);
                Assert.AreEqual(File.GetCreationTimeUtc(path), baseline.CreatedUtc);
                Assert.IsTrue(baseline.ObservationSequence > 0);
                Assert.IsTrue(ledger.Capture().Outputs.Single().Current.ObservationSequence > baseline.ObservationSequence);
                string absent = Path.Combine(workspace.Folder, "missing", "nested");
                var missing = new OutputOwnershipLedger(Package("SavedWordTemplate", "Notes.dotx"), workspace.Folder, absent);
                Assert.IsFalse(missing.Capture().Outputs.Single().Baseline.Exists);
                Assert.IsFalse(Directory.Exists(absent));
            }
        }

        [TestMethod]
        public void CreatedClaimedUnchangedOutputIsDeletedOnlyAfterOwnedWordCloses()
        {
            using (var workspace = new TestWorkspace())
            {
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                string path = Write(workspace, "answer.txt", "session answer");
                ledger.ObserveOwnedDocument(path);
                ledger.Cleanup(false);
                Assert.IsTrue(File.Exists(path));
                ledger.Cleanup(true);
                Assert.IsFalse(File.Exists(path));
                ledger.Cleanup(true);
            }
        }

        [TestMethod]
        public void NewlyCreatedDeclaredOutputIsClaimedAtCleanupWithoutOwnedDocumentObservation()
        {
            using (var workspace = new TestWorkspace())
            {
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                string path = Write(workspace, "answer.txt", "external");
                ledger.Capture(Path.Combine(workspace.Folder, "unrelated.docx"));
                ledger.Cleanup(true);
                Assert.IsFalse(File.Exists(path));
            }
        }

        [TestMethod]
        public void FutureDatedPreexistingOutputRemainsProtected()
        {
            using (var workspace = new TestWorkspace())
            {
                string path = Write(workspace, "answer.txt", "personal");
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(1));
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                ledger.Cleanup(true);
                Assert.AreEqual("personal", File.ReadAllText(path));
            }
        }

        [TestMethod]
        public void OlderFileMovedIntoAbsentDeclaredPathIsNotClaimedAtCleanup()
        {
            using (var workspace = new TestWorkspace())
            {
                string source = Write(workspace, "personal.txt", "personal");
                File.SetCreationTimeUtc(source, DateTime.UtcNow.AddDays(-1));
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                string path = Path.Combine(workspace.Folder, "answer.txt");
                File.Move(source, path);
                ledger.Cleanup(true);
                Assert.AreEqual("personal", File.ReadAllText(path));
            }
        }

        [DataTestMethod]
        [DataRow("unchanged")]
        [DataRow("modified")]
        [DataRow("replaced")]
        [DataRow("missing")]
        public void OutputClaimsAreJournaledImmediatelyAndForgottenAtTerminalCleanup(string scenario)
        {
            using (var workspace = new TestWorkspace())
            {
                var journal = new SessionRecoveryJournal(Path.Combine(workspace.Folder, "journal.json"), _ => { });
                var ledger = new OutputOwnershipLedger(Package("FileExistsInDocuments", "answer.txt"),
                    workspace.Folder, null, _ => { }, journal: journal);
                string path = Write(workspace, "answer.txt", "original");
                ledger.ObserveOwnedDocument(path);
                Assert.AreEqual(1, new SessionRecoveryJournal(Path.Combine(workspace.Folder, "journal.json"), _ => { }).Count);
                if (scenario == "modified") File.WriteAllText(path, "personal edit");
                if (scenario == "replaced")
                {
                    File.Move(path, Path.Combine(workspace.Folder, "held.txt"));
                    File.WriteAllText(path, "original");
                }
                if (scenario == "missing") File.Delete(path);
                ledger.Cleanup(true);
                Assert.AreEqual(0, journal.Count);
                Assert.AreEqual(scenario == "modified" || scenario == "replaced", File.Exists(path));
            }
        }

        [TestMethod]
        public void CleanupTimeClaimIsPersistedBeforeLockedDeleteAndRecoveredLater()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = Path.Combine(workspace.Folder, "journal.json");
                var journal = new SessionRecoveryJournal(journalPath, _ => { });
                var ledger = new OutputOwnershipLedger(Package("FileExistsInDocuments", "answer.txt"),
                    workspace.Folder, null, _ => { }, journal: journal);
                string path = Write(workspace, "answer.txt", "new unobserved output");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    ledger.Cleanup(true);
                    Assert.IsTrue(File.Exists(path));
                    Assert.AreEqual(1, journal.Count);
                    var recovery = new SessionRecoveryJournal(journalPath, _ => { });
                    recovery.Recover();
                    Assert.AreEqual(1, recovery.Count);
                }
                var retry = new SessionRecoveryJournal(journalPath, _ => { });
                retry.Recover();
                Assert.AreEqual(0, retry.Count);
                Assert.IsFalse(File.Exists(path));
            }
        }

        [TestMethod]
        public void NewAbsentBaselineCanReplaceAnObsoletePriorSessionJournalClaim()
        {
            using (var workspace = new TestWorkspace())
            {
                string journalPath = Path.Combine(workspace.Folder, "journal.json");
                var journal = new SessionRecoveryJournal(journalPath, _ => { });
                var package = Package("FileExistsInDocuments", "answer.txt");
                var previous = new OutputOwnershipLedger(package, workspace.Folder, null, _ => { }, journal: journal);
                string path = Write(workspace, "answer.txt", "previous session output");
                previous.ObserveOwnedDocument(path);
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    previous.Cleanup(true);
                Assert.AreEqual(1, journal.Count);
                File.Delete(path);
                var current = new OutputOwnershipLedger(package, workspace.Folder, null, _ => { }, journal: journal);
                Assert.IsFalse(current.Capture().Outputs.Single().Baseline.Exists);
                Write(workspace, "answer.txt", "new session output");
                current.ObserveOwnedDocument(path);
                var saved = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(journalPath));
                Assert.AreEqual(current.SessionId, (string)saved["entries"][0]["sessionId"]);
                Assert.AreEqual(1, journal.Count);
                current.Cleanup(true);
                Assert.IsFalse(File.Exists(path));
                Assert.AreEqual(0, journal.Count);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void PreexistingOutputRemainsProtectedWhetherOwnedWordModifiesIt(bool modify)
        {
            using (var workspace = new TestWorkspace())
            {
                string path = Write(workspace, "answer.txt", "personal");
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                if (modify) File.WriteAllText(path, "edited personal");
                ledger.Capture(path);
                ledger.Cleanup(true);
                Assert.AreEqual(modify ? "edited personal" : "personal", File.ReadAllText(path));
            }
        }

        [TestMethod]
        public void FirstClaimIsNotRefreshedAfterModification()
        {
            using (var workspace = new TestWorkspace())
            {
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                string path = Write(workspace, "answer.txt", "first");
                ledger.ObserveOwnedDocument(path);
                File.WriteAllText(path, "personal edit");
                ledger.Capture(path);
                ledger.Cleanup(true);
                Assert.AreEqual("personal edit", File.ReadAllText(path));
            }
        }

        [TestMethod]
        public void ReplacedOutputWithIdenticalBytesIsPreserved()
        {
            using (var workspace = new TestWorkspace())
            {
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                string path = Write(workspace, "answer.txt", "same bytes");
                ledger.ObserveOwnedDocument(path);
                string originalIdentity = ledger.Capture().Outputs.Single().Current.Identity;
                File.Move(path, Path.Combine(workspace.Folder, "original-held.txt"));
                File.WriteAllText(path, "same bytes");
                Assert.AreNotEqual(originalIdentity, ledger.Capture(path).Outputs.Single().Current.Identity);
                ledger.Cleanup(true);
                Assert.AreEqual("same bytes", File.ReadAllText(path));
            }
        }

        [TestMethod]
        public void SavedOutputCanBeObservedWhileOwnedWordRetainsASharedWriteHandle()
        {
            using (var workspace = new TestWorkspace())
            {
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                string path = Path.Combine(workspace.Folder, "answer.txt");
                using (var owned = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes("saved live owned output");
                    owned.Write(bytes, 0, bytes.Length);
                    owned.Flush(true);
                    var output = ledger.Capture(path).Outputs.Single();
                    Assert.IsNull(output.ObservationError);
                    Assert.IsNotNull(output.Current);
                    Assert.IsTrue(output.Current.Exists);
                    Assert.AreEqual(bytes.Length, output.Current.Length);
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        Assert.AreEqual(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""), output.Current.Sha256);
                    ledger.Cleanup(true);
                    Assert.IsTrue(File.Exists(path), "Cleanup must still use an exclusive handle.");
                }
                ledger.Cleanup(true);
                Assert.IsFalse(File.Exists(path));
            }
        }

        [TestMethod]
        public void LockedOutputIsPreservedAndCurrentReadFailureIsExplicit()
        {
            using (var workspace = new TestWorkspace())
            {
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                string path = Write(workspace, "answer.txt", "answer");
                ledger.ObserveOwnedDocument(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var output = ledger.Capture().Outputs.Single();
                    Assert.IsNull(output.Current);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(output.ObservationError));
                    ledger.Cleanup(true);
                    Assert.IsTrue(File.Exists(path));
                    Assert.ThrowsException<Win32Exception>(() => Ledger(workspace, Package("FileExistsInDocuments", "answer.txt")));
                }
                Assert.IsTrue(File.Exists(path));
            }
        }

        [TestMethod]
        public void ReadOnlyClaimedOutputIsPreserved()
        {
            using (var workspace = new TestWorkspace())
            {
                var ledger = Ledger(workspace, Package("FileExistsInDocuments", "answer.txt"));
                string path = Write(workspace, "answer.txt", "answer");
                ledger.ObserveOwnedDocument(path);
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try { ledger.Cleanup(true); Assert.IsTrue(File.Exists(path)); }
                finally { File.SetAttributes(path, FileAttributes.Normal); }
            }
        }

        [DataTestMethod]
        [DataRow("../bad.txt")]
        [DataRow("sub\\bad.txt")]
        [DataRow("CON.txt")]
        [DataRow("LPT1.txt")]
        [DataRow("answer.txt.")]
        [DataRow("answer.txt ")]
        [DataRow("answer.txt:stream")]
        public void UnsafeNamesAreRejected(string name)
        {
            using (var workspace = new TestWorkspace())
                Assert.ThrowsException<InvalidDataException>(() => Ledger(workspace, Package("FileExistsInDocuments", name)));
        }

        [TestMethod]
        public void CaseInsensitiveCollisionsAreOneEntryAndArbitraryP16OutputIsExcluded()
        {
            using (var workspace = new TestWorkspace())
            {
                var package = Package("FileExistsInDocuments", "Answer.txt");
                package.Tasks.Add(Task("PlainTextDocumentExport", "answer.TXT"));
                package.Tasks.Add(Task("PlainTextAtOwnedSavedLocation", "arbitrary.txt"));
                var ledger = Ledger(workspace, package);
                Assert.AreEqual(1, ledger.Capture().Outputs.Count);
                string arbitrary = Write(workspace, "arbitrary.txt", "personal arbitrary folder export");
                ledger.ObserveOwnedDocument(arbitrary);
                ledger.Cleanup(true);
                Assert.IsTrue(File.Exists(arbitrary));
            }
        }

        [TestMethod]
        public void P16DoesNotRequireOrProbeATemplateRoot()
        {
            using (var workspace = new TestWorkspace())
            {
                var ledger = new OutputOwnershipLedger(Package("PlainTextAtOwnedSavedLocation", "arbitrary.txt"),
                    workspace.Folder, null, message => { });
                Assert.IsNull(ledger.TemplateFolder);
                Assert.AreEqual(0, ledger.Capture().Outputs.Count);
                ledger.Cleanup(true);
            }
        }

        [DataTestMethod]
        [DataRow("relative")]
        [DataRow(@"C:relative")]
        [DataRow(@"\relative")]
        public void DocumentsRootMustBeAbsolute(string folder)
        {
            Assert.ThrowsException<InvalidDataException>(() => new OutputOwnershipLedger(
                Package("PlainTextAtOwnedSavedLocation", "arbitrary.txt"), folder, null));
        }

        [TestMethod]
        public void NullFilenameMetadataIsRejectedExplicitly()
        {
            using (var workspace = new TestWorkspace())
            {
                var package = Package("FileExistsInDocuments", "answer.txt");
                package.Tasks[0].Extra["expectedFileName"] = null;
                Assert.ThrowsException<InvalidDataException>(() => Ledger(workspace, package));
            }
        }

        [TestMethod]
        public void P20SavedTemplateIsDeclaredAndFreshP07LedgerSeesStaleBaseline()
        {
            using (var workspace = new TestWorkspace())
            {
                var p20 = Ledger(workspace, Package("SavedWordTemplate", "Notes.dotx"));
                string folder = Path.Combine(workspace.Folder, "Templates");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "Notes.dotx");
                File.WriteAllText(path, "template bytes");
                p20.Capture(path);
                var p07 = Ledger(workspace, Package("FileExistsInCustomOfficeTemplates", "Notes.dotx"));
                Assert.IsTrue(p07.Capture().Outputs.Single().Baseline.Exists);
                p07.ObserveOwnedDocument(path);
                p07.Cleanup(true);
                Assert.IsTrue(File.Exists(path));
                p20.Cleanup(true);
                Assert.IsFalse(File.Exists(path));
            }
        }

        private static OutputOwnershipLedger Ledger(TestWorkspace workspace, ProjectPackage package) =>
            new OutputOwnershipLedger(package, workspace.Folder, Path.Combine(workspace.Folder, "Templates"), message => { });

        private static ProjectPackage Package(string assertion, string name)
        {
            var package = new ProjectPackage();
            package.Tasks.Add(Task(assertion, name));
            return package;
        }

        private static TaskDefinition Task(string assertion, string name)
        {
            var task = new TaskDefinition { AssertionType = assertion };
            task.Extra["expectedFileName"] = name;
            return task;
        }

        private static string Write(TestWorkspace workspace, string name, string text)
        {
            string path = Path.Combine(workspace.Folder, name);
            File.WriteAllText(path, text);
            return path;
        }
    }
}
