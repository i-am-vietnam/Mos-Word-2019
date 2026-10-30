using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Projects
{
    public sealed class PreparedTestingSession
    {
        public string SessionId { get; }
        public string Language { get; }
        public ExamDefinition Definition { get; }
        public string CatalogPath { get; }
        public IReadOnlyDictionary<string, string> ContentHashes { get; }
        internal PreparedTestingSession(string id, string language, ExamDefinition definition, string catalog, Dictionary<string, string> hashes)
        {
            SessionId = id; Language = language; Definition = definition; CatalogPath = catalog;
            ContentHashes = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(hashes));
        }
        internal void VerifyCatalog() { Verify("testing-catalog.json", CatalogPath); }
        internal void VerifyPackage(ProjectPackage package)
        {
            Verify(package.Meta.ProjectId + "/tasks.json", Path.Combine(package.ProjectFolderPath, "tasks.json"));
            Verify(package.Meta.ProjectId + "/" + package.Meta.Starter, Path.Combine(package.ProjectFolderPath, package.Meta.Starter));
        }
        private void Verify(string key, string path)
        {
            string expected;
            if (!ContentHashes.TryGetValue(key, out expected) || Hash(path) != expected)
                throw new InvalidDataException("Audited Testing content has changed: " + key);
        }
        internal static string Hash(string path)
        {
            CheckPath(path);
            using (var hash = SHA256.Create())
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
        }
        internal static void CheckPath(string path)
        {
            for (string p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Testing state/content path traverses a reparse point: " + p);
        }
    }

    /// <summary>Durable preparation/audit and write-only snapshots. Resume hydration is a separate integration step.</summary>
    public sealed partial class TestingSessionStateStore
    {
        private readonly object gate = new object();
        public string StatePath { get; }
        public bool RequiresRecovery
        {
            get
            {
                lock (gate)
                {
                    if (!File.Exists(StatePath)) return false;
                    string status = (string)ReadEnvelope()["status"];
                    return status != "Preparing" && status != "Prepared";
                }
            }
        }
        public TestingSessionStateStore(string statePath = null)
        {
            StatePath = Path.GetFullPath(statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MosWord2019", "testing-session.json"));
        }
        public PreparedTestingSession Prepare(string catalogPath, IEnumerable<ProjectPackage> packages, string language)
        { lock (gate) return PrepareCore(catalogPath, packages, language); }
        private PreparedTestingSession PrepareCore(string catalogPath, IEnumerable<ProjectPackage> packages, string language)
        {
            if (language != "en" && language != "vi") throw new ArgumentException("Testing language must be en or vi.");
            JObject prior = File.Exists(StatePath) ? ReadEnvelope() : null;
            if ((string)prior?["status"] == "Completed") { ArchiveCompleted(); prior = null; }
            if ((string)prior?["status"] == "Prepared") return ReadPrepared(prior, catalogPath, packages);
            if (prior != null && (string)prior["status"] != "Preparing") throw new IOException("An active Testing session requires resume/finalization.");
            byte[] bytes = new byte[8];
            using (var random = new RNGCryptoServiceProvider()) random.GetBytes(bytes);
            long seed = prior == null ? BitConverter.ToInt64(bytes, 0) : (long)prior["seed"];
            string id = prior == null ? Guid.NewGuid().ToString("N") : (string)prior["sessionId"];
            // Persist the cryptographic seed before composing, hashing, staging, or starting the timer.
            if (prior == null) Write(new JObject { ["version"] = 1, ["sessionId"] = id, ["seed"] = seed,
                ["language"] = language, ["machine"] = Environment.MachineName, ["status"] = "Preparing" }, false);
            else if (prior["language"] != null && (string)prior["language"] != language)
                throw new InvalidDataException("Prepared exam language cannot change on retry.");
            var available = packages.ToList();
            string catalog = Path.GetFullPath(catalogPath), catalogHash = PreparedTestingSession.Hash(catalog);
            var entries = new TestingCatalogLoader().Load(catalog, available);
            var definition = ExamComposer.Compose(entries, available, seed);
            var hashes = new Dictionary<string, string> { { "testing-catalog.json", catalogHash } };
            foreach (var selected in definition.Projects)
            {
                var source = available.Single(p => p.Meta.ProjectId == selected.ProjectId);
                var reloaded = new ProjectLoader().Load(source.ProjectFolderPath, language);
                if (reloaded == null || reloaded.Meta.ProjectId != source.Meta.ProjectId || reloaded.Meta.Starter != source.Meta.Starter ||
                    !JToken.DeepEquals(JToken.FromObject(source.Tasks), JToken.FromObject(reloaded.Tasks)))
                    throw new InvalidDataException("Package metadata changed; reload before preparing Testing.");
                hashes.Add(selected.ProjectId + "/tasks.json", PreparedTestingSession.Hash(Path.Combine(source.ProjectFolderPath, "tasks.json")));
                hashes.Add(selected.ProjectId + "/" + source.Meta.Starter, PreparedTestingSession.Hash(Path.Combine(source.ProjectFolderPath, source.Meta.Starter)));
            }
            var prepared = new PreparedTestingSession(id, language, definition, catalog, hashes);
            prepared.VerifyCatalog();
            Save(prepared, null, null);
            return prepared;
        }
        public void Save(PreparedTestingSession prepared, TestingSession session, DateTime? lastSaveUtc)
        { lock (gate) SaveCore(prepared, session, lastSaveUtc); }
        private void SaveCore(PreparedTestingSession prepared, TestingSession session, DateTime? lastSaveUtc)
        {
            if (prepared == null || (session != null && (session.SessionId != prepared.SessionId || session.Definition != prepared.Definition)))
                throw new InvalidDataException("Testing snapshot does not match its prepared definition.");
            PreparedTestingSession.CheckPath(StatePath);
            var prior = JObject.Parse(File.ReadAllText(StatePath));
            if ((string)prior["sessionId"] != prepared.SessionId) throw new InvalidDataException("Testing state belongs to another session.");
            if (lastSaveUtc.HasValue && lastSaveUtc.Value.Kind != DateTimeKind.Utc) throw new ArgumentException("Last save time must be UTC.");
            var snapshot = session?.CaptureState();
            var data = new JObject
            {
                ["version"] = 1, ["sessionId"] = prepared.SessionId, ["seed"] = prepared.Definition.Seed,
                ["machine"] = Environment.MachineName, ["language"] = prepared.Language,
                ["definition"] = JObject.FromObject(prepared.Definition), ["contentHashes"] = JObject.FromObject(prepared.ContentHashes),
                ["startedUtc"] = snapshot?.StartedUtc.HasValue == true ? new JValue(snapshot.StartedUtc.Value) : JValue.CreateNull(),
                ["completedUtc"] = snapshot?.CompletedUtc.HasValue == true ? new JValue(snapshot.CompletedUtc.Value) : JValue.CreateNull(),
                ["offlinePeriods"] = JArray.FromObject(snapshot?.OfflinePeriods ?? new TestingOfflinePeriod[0]),
                ["historyIncomplete"] = snapshot?.HistoryIncomplete ?? false,
                ["lastHeartbeatUtc"] = DateTime.UtcNow, ["lastSaveUtc"] = lastSaveUtc.HasValue ? new JValue(lastSaveUtc.Value) : JValue.CreateNull(),
                ["finalizeReason"] = snapshot?.FinalizeReason.ToString() ?? "None",
                ["clockRollbackDetected"] = (bool?)prior["clockRollbackDetected"] ?? false,
                ["ownedWord"] = prior["ownedWord"]?.DeepClone() ?? JValue.CreateNull(),
                ["projectContexts"] = prior["projectContexts"]?.DeepClone() ?? new JObject(),
                ["offlineElapsedTicks"] = snapshot?.OfflineElapsed.Ticks ?? 0,
                ["elapsedTicks"] = snapshot?.Elapsed.Ticks ?? 0, ["currentIndex"] = snapshot?.CurrentIndex ?? 0,
                ["status"] = snapshot == null ? "Prepared" : snapshot.Status.ToString(),
                ["projectStates"] = JArray.FromObject(snapshot?.ProjectStates ?? Enumerable.Repeat(TestingProjectState.NotStarted, 7).ToArray()),
                ["lockedResults"] = JArray.FromObject(snapshot?.LockedResults ?? new ExamTaskGrade[0]),
                ["result"] = snapshot?.Result == null ? JValue.CreateNull() : JToken.FromObject(snapshot.Result)
            };
            Write(data, true);
        }
        private void Write(JObject data, bool replace)
        {
            PreparedTestingSession.CheckPath(StatePath);
            string folder = Path.GetDirectoryName(StatePath);
            Directory.CreateDirectory(folder); PreparedTestingSession.CheckPath(StatePath);
            string temporary = Path.Combine(folder, ".testing-state-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(data.ToString());
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                MosWord2019.Core.IO.AtomicFile.Commit(temporary, StatePath, replace, () => PreparedTestingSession.CheckPath(StatePath));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
