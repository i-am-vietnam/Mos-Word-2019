using System;
using System.Collections.Generic;
using System.IO;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using Newtonsoft.Json.Linq;
namespace MosWord2019.Projects
{
    public enum TestingResumeChoice { Resume, Submit, Later }
    public sealed class TestingStartupRecovery
    {
        private readonly TestingSessionStateStore store;
        private readonly SessionRecoveryJournal journal;
        public TestingStartupRecovery(TestingSessionStateStore store, SessionRecoveryJournal journal) { this.store = store; this.journal = journal; }
        public TestingRecoveryRecord Inspect(string catalog, IEnumerable<ProjectPackage> packages, DateTime nowUtc,
            Func<OwnedWordProcessIdentity, string, bool> isRunning, Func<OwnedWordProcessIdentity, bool> confirmClose,
            Func<OwnedWordProcessIdentity, string, bool> stop, Func<TestingRecoveryRecord, TestingResumeChoice> choose,
            Action<TestingRecoveryRecord> finalize)
        {
            store.ProtectPendingRecovery(journal);
            if (!File.Exists(store.StatePath)) return null;
            var envelope = store.ReadEnvelope(); string status = (string)envelope["status"];
            if (status == "Preparing" || status == "Prepared") return null; // Start retries the original prepared exam.
            var identity = envelope["ownedWord"]?.Type == JTokenType.Object ? envelope["ownedWord"].ToObject<OwnedWordProcessIdentity>() : null;
            string machine = (string)envelope["machine"];
            journal.DeferRecovery(true);
            if (identity != null && isRunning(identity, machine))
            {
                if (!confirmClose(identity)) return null; // No finalization, mutation or recovery after a refusal.
                if (!stop(identity, machine)) return null; // Refusal/race leaves the session pending; retry safely next start.
            }
            if (status == "Completed")
            { store.ArchiveCompleted(); Release((string)envelope["sessionId"]); return null; }
            var record = store.Load(catalog, packages); var decision = record.Decide(nowUtc);
            store.RecordRecoveryDecision(decision);
            var choice = decision.Action == TestingRecoveryAction.Resume ? choose(record) : TestingResumeChoice.Submit;
            if (choice == TestingResumeChoice.Later) return null;
            if (choice == TestingResumeChoice.Resume)
            {
                journal.DeferRecovery(false); journal.Recover(); // Its own SessionId remains protected.
                return record;
            }
            Complete(record, finalize);
            return null;
        }
        /// <summary>Explicit fallback: no process inspection, termination or Word activation.</summary>
        public void FinalizeSaved(string catalog, IEnumerable<ProjectPackage> packages, Action<TestingRecoveryRecord> finalize)
        {
            store.ProtectPendingRecovery(journal); journal.DeferRecovery(true);
            var envelope = store.ReadEnvelope();
            if ((string)envelope["status"] == "Completed")
            { store.ArchiveCompleted(); Release((string)envelope["sessionId"]); return; }
            var record = store.Load(catalog, packages);
            store.RecordRecoveryDecision(record.Decide(DateTime.UtcNow));
            Complete(record, finalize);
        }
        private void Complete(TestingRecoveryRecord record, Action<TestingRecoveryRecord> finalize)
        {
            finalize(record); // Must store and archive before any recovery claims are released.
            if (File.Exists(store.StatePath)) store.ArchiveCompleted(); // Refuse recovery if finalization did not actually store a completed result.
            string resultPath = Path.Combine(Path.GetDirectoryName(store.StatePath), "Results", record.Prepared.SessionId + ".json");
            PreparedTestingSession.CheckPath(resultPath);
            if (!File.Exists(resultPath)) throw new InvalidDataException("Testing finalization did not archive its result.");
            Release(record.Prepared.SessionId);
        }
        public void Release(string id) { journal.ReleaseSession(id); journal.DeferRecovery(false); journal.Recover(); }
    }
}
