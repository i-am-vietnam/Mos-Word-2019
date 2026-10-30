using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Projects
{
    public sealed class TestingRecoveryRecord
    {
        public PreparedTestingSession Prepared { get; }
        public TestingSessionSnapshot Snapshot { get; }
        public DateTime LastHeartbeatUtc { get; }
        public OwnedWordProcessIdentity OwnedWord { get; }
        public string Machine { get; }
        internal TestingRecoveryRecord(PreparedTestingSession prepared, TestingSessionSnapshot snapshot, DateTime heartbeat, string machine, OwnedWordProcessIdentity ownedWord)
        { Prepared = prepared; Snapshot = snapshot; LastHeartbeatUtc = heartbeat; Machine = machine; OwnedWord = ownedWord; }
        public TestingRecoveryDecision Decide(DateTime nowUtc)
        {
            var decision = TestingRecoveryDecision.Evaluate(Snapshot.Status, Snapshot.Elapsed, LastHeartbeatUtc, nowUtc,
                string.Equals(Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
            if (Snapshot.Status == TestingSessionStatus.Offline && Snapshot.OfflineElapsed + decision.Downtime >= TestingSession.OfflineLimit)
                return TestingRecoveryDecision.Evaluate(TestingSessionStatus.Submitting, Snapshot.Elapsed, LastHeartbeatUtc, nowUtc, true);
            return decision;
        }
        public TestingSession Restore(ITestingClock clock, IConnectivityMonitor monitor) => TestingSession.Restore(Prepared.SessionId,
            Prepared.Definition, Snapshot, Decide(clock.UtcNow), clock, monitor);
    }

    public sealed partial class TestingSessionStateStore
    {
        internal JObject ReadEnvelope()
        {
            PreparedTestingSession.CheckPath(StatePath);
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(StatePath), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
            catch (JsonException ex) { throw new InvalidDataException("Invalid Testing state JSON.", ex); }
            Guid id;
            if (root["version"]?.Type != JTokenType.Integer || (int)root["version"] != 1 ||
                root["seed"]?.Type != JTokenType.Integer || root["sessionId"]?.Type != JTokenType.String ||
                !Guid.TryParseExact((string)root["sessionId"], "N", out id) || root["status"]?.Type != JTokenType.String ||
                !new[] { "Preparing", "Prepared", "Running", "Offline", "Submitting", "Completed" }.Contains((string)root["status"]))
                throw new InvalidDataException("Invalid Testing state envelope.");
            return root;
        }
        private PreparedTestingSession ReadPrepared(JObject data, string catalogPath, IEnumerable<ProjectPackage> packages)
        {
            var definition = data["definition"] as JObject; var selected = definition?["Projects"] as JArray;
            var hashes = data["contentHashes"] as JObject;
            if (selected == null || hashes == null || hashes.Count != 15 || hashes.Properties().Any(p => p.Value.Type != JTokenType.String ||
                ((string)p.Value).Length != 64 || ((string)p.Value).Any(c => !Uri.IsHexDigit(c))) ||
                !new[] { "en", "vi" }.Contains((string)data["language"])) throw new InvalidDataException("Invalid Testing definition/audit.");
            var available = packages.ToDictionary(p => p.Meta.ProjectId, StringComparer.Ordinal);
            var projects = new List<ExamProjectDefinition>();
            foreach (var item in selected)
            {
                ProjectPackage package;
                var obj = item as JObject; var ids = obj?["TaskIds"] as JArray; var keys = obj?["InstructionKeys"] as JObject;
                string id = (string)obj?["ProjectId"];
                if (id == null || !available.TryGetValue(id, out package) || ids == null || keys == null ||
                    ids.Any(t => t.Type != JTokenType.String) || keys.Properties().Any(p => p.Value.Type != JTokenType.String))
                    throw new InvalidDataException("Invalid saved Testing project.");
                var tasks = ids.Select(t => new TaskDefinition { TaskId = (string)t, InstructionKey = (string)keys[(string)t] }).ToList();
                if (!package.Tasks.Where(t => tasks.Any(s => s.TaskId == t.TaskId)).Select(t => t.TaskId).SequenceEqual(tasks.Select(t => t.TaskId)) ||
                    tasks.Any(t => !package.Tasks.Any(p => p.TaskId == t.TaskId && p.InstructionKey == t.InstructionKey)))
                    throw new InvalidDataException("Saved tasks no longer match the package.");
                projects.Add(new ExamProjectDefinition(id, tasks));
            }
            long seed = (long)data["seed"];
            var exam = new ExamDefinition(seed, projects);
            if ((long?)definition["Seed"] != seed || (int?)definition["N"] != exam.N) throw new InvalidDataException("Saved exam totals/seed are inconsistent.");
            var prepared = new PreparedTestingSession((string)data["sessionId"], (string)data["language"], exam, Path.GetFullPath(catalogPath),
                hashes.Properties().ToDictionary(p => p.Name, p => (string)p.Value));
            prepared.VerifyCatalog(); foreach (var project in projects) prepared.VerifyPackage(available[project.ProjectId]);
            return prepared;
        }
        public TestingRecoveryRecord Load(string catalogPath, IEnumerable<ProjectPackage> packages)
        {
            try { return LoadCore(catalogPath, packages); }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException) && !(ex is InvalidDataException))
            { throw new InvalidDataException("Invalid Testing recovery state.", ex); }
        }
        private TestingRecoveryRecord LoadCore(string catalogPath, IEnumerable<ProjectPackage> packages)
        {
            lock (gate)
            {
                var data = ReadEnvelope(); var prepared = ReadPrepared(data, catalogPath, packages);
                TestingSessionStatus status; TestingFinalizeReason reason;
                string value = (string)data["status"];
                if (value == "Prepared") status = TestingSessionStatus.Ready;
                else if (!Enum.TryParse(value, out status) || !Enum.IsDefined(typeof(TestingSessionStatus), status)) throw new InvalidDataException("Invalid saved status.");
                if (!Enum.TryParse((string)data["finalizeReason"], out reason) || !Enum.IsDefined(typeof(TestingFinalizeReason), reason)) throw new InvalidDataException("Invalid finalization reason.");
                var states = data["projectStates"] as JArray; var grades = data["lockedResults"] as JArray;
                if (states == null || states.Count != 7 || states.Any(t => t.Type != JTokenType.Integer || !Enum.IsDefined(typeof(TestingProjectState), (int)t)) ||
                    grades == null || grades.Any(g => g is JObject == false || g["ProjectId"]?.Type != JTokenType.String ||
                        g["TaskId"]?.Type != JTokenType.String || g["Result"] is JObject == false ||
                        g["Result"]["Outcome"]?.Type != JTokenType.Integer || !Enum.IsDefined(typeof(TaskGradeOutcome), (int)g["Result"]["Outcome"]) ||
                        g["Result"]["TaskId"]?.Type != JTokenType.String || (string)g["Result"]["TaskId"] != (string)g["TaskId"]) || data["elapsedTicks"]?.Type != JTokenType.Integer || data["currentIndex"]?.Type != JTokenType.Integer ||
                    data["lastHeartbeatUtc"]?.Type != JTokenType.Date || ((DateTime)data["lastHeartbeatUtc"]).Kind != DateTimeKind.Utc ||
                    data["machine"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)data["machine"])) throw new InvalidDataException("Invalid saved Testing state.");
                var snapshot = new TestingSessionSnapshot(status, reason, (int)data["currentIndex"], TimeSpan.FromTicks((long)data["elapsedTicks"]),
                    states.Select(t => (TestingProjectState)(int)t), grades.ToObject<List<ExamTaskGrade>>(), null,
                    TimeSpan.FromTicks((long?)data["offlineElapsedTicks"] ?? 0), (DateTime?)data["startedUtc"], (DateTime?)data["completedUtc"],
                    data["offlinePeriods"]?.ToObject<List<TestingOfflinePeriod>>(), (bool?)data["historyIncomplete"] ?? data["offlinePeriods"] == null);
                var owned = data["ownedWord"] == null || data["ownedWord"].Type == JTokenType.Null ? null : data["ownedWord"].ToObject<OwnedWordProcessIdentity>();
                var record = new TestingRecoveryRecord(prepared, snapshot, (DateTime)data["lastHeartbeatUtc"], (string)data["machine"], owned);
                var decision = record.Decide(DateTime.UtcNow);
                if (decision.Action == TestingRecoveryAction.Resume || decision.Action == TestingRecoveryAction.Finalize)
                    using (var checkedState = TestingSession.Restore(prepared.SessionId, prepared.Definition, snapshot, decision,
                        new StopwatchTestingClock(), new AlwaysOnlineMonitor())) { }
                return record;
            }
        }
        public void RecordProjectContext(PreparedTestingSession prepared, string projectId, string sourcePath,
            TrainingGradeContext context, DateTime? savedUtc)
        {
            if (prepared == null || context == null || !prepared.Definition.Projects.Any(p => p.ProjectId == projectId) ||
                context.OutputOwnership?.SessionId != prepared.SessionId || !Path.IsPathRooted(sourcePath) ||
                savedUtc.HasValue && savedUtc.Value.Kind != DateTimeKind.Utc) throw new InvalidDataException("Invalid Testing project context.");
            lock (gate)
            {
                var data = ReadEnvelope();
                if ((string)data["sessionId"] != prepared.SessionId) throw new InvalidDataException("Project context/session mismatch.");
                var contexts = data["projectContexts"] as JObject ?? new JObject();
                contexts[projectId] = new JObject { ["sourcePath"] = sourcePath, ["context"] = JObject.FromObject(context),
                    ["savedUtc"] = savedUtc.HasValue ? new JValue(savedUtc.Value) : JValue.CreateNull() };
                data["projectContexts"] = contexts;
                if (savedUtc.HasValue) data["lastSaveUtc"] = savedUtc.Value;
                Write(data, true);
            }
        }

        public TrainingGradeContext ReadProjectContext(string projectId)
        {
            lock (gate)
            {
                var value = ReadEnvelope()["projectContexts"]?[projectId]?["context"];
                return value == null ? null : value.ToObject<TrainingGradeContext>();
            }
        }
        public void RecordRecoveryDecision(TestingRecoveryDecision decision)
        {
            if (!decision.SuspiciousClockRollback) return;
            lock (gate) { var data = ReadEnvelope(); data["clockRollbackDetected"] = true; Write(data, true); }
        }
        internal void SaveHeartbeat(PreparedTestingSession prepared, TestingSession session)
        {
            lock (gate)
            {
                var prior = ReadEnvelope();
                SaveCore(prepared, session, prior["lastSaveUtc"]?.Type == JTokenType.Date ? (DateTime?)prior["lastSaveUtc"] : null);
            }
        }
        public void RecordOwnedWord(PreparedTestingSession prepared, OwnedWordProcessIdentity identity)
        {
            if (identity == null || !identity.IsWord) throw new ArgumentException("A verified owned Word identity is required.");
            lock (gate)
            {
                var data = ReadEnvelope();
                if ((string)data["sessionId"] != prepared.SessionId) throw new InvalidDataException("Word process/session mismatch.");
                data["ownedWord"] = JObject.FromObject(identity); Write(data, true);
            }
        }
        public string ArchiveCompleted()
        {
            lock (gate)
            {
                string target = PersistCompletedResult();
                PreparedTestingSession.CheckPath(StatePath); File.Delete(StatePath); // Remove recovery input only after the result is durable.
                return target;
            }
        }
        /// <summary>Call before every generic journal recovery; pending active exams must be handled first.</summary>
        public void ProtectPendingRecovery(SessionRecoveryJournal journal)
        {
            lock (gate)
            {
                try { File.GetAttributes(StatePath); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException))
                { journal.DeferRecovery(true); MosWord2019.Core.Diagnostics.AppLogger.Write("Testing state cannot be inspected; recovery deferred", ex); return; }
                try
                {
                    var data = ReadEnvelope();
                    if ((string)data["status"] == "Completed" && data["ownedWord"]?.Type == JTokenType.Null) { ArchiveCompleted(); return; }
                    journal.PreserveSession((string)data["sessionId"]);
                    if ((string)data["status"] != "Prepared" && (string)data["status"] != "Preparing") journal.DeferRecovery(true);
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException) && !(ex is StackOverflowException))
                { journal.DeferRecovery(true); MosWord2019.Core.Diagnostics.AppLogger.Write("Testing recovery requires attention; journal retained", ex); }
            }
        }
    }
}
