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
using MosWord2019.Projects.OnlinePackages;

namespace MosWord2019.Projects
{
    public sealed class PreparedTestingSession
    {
        public string SessionId { get; }
        public string Language { get; }
        public ExamDefinition Definition { get; }
        public string CatalogPath { get; }
        public bool IsOnline => DefinitionHash != null;
        public string DefinitionHash { get; }
        internal JObject OnlinePackages { get; }
        internal JArray RecoveryPackages { get; }
        public IReadOnlyDictionary<string, string> ContentHashes { get; }
        internal PreparedTestingSession(string id, string language, ExamDefinition definition, string catalog, Dictionary<string, string> hashes, string definitionHash = null, JObject onlinePackages = null, JArray recoveryPackages = null)
        {
            SessionId = id; Language = language; Definition = definition; CatalogPath = catalog;
            DefinitionHash = definitionHash; OnlinePackages = onlinePackages == null ? null : (JObject)onlinePackages.DeepClone();
            RecoveryPackages = recoveryPackages == null ? null : (JArray)recoveryPackages.DeepClone();
            ContentHashes = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(hashes));
        }
        internal void VerifyCatalog()
        {
            if (IsOnline)
            {
                if (ExamDefinitionFingerprint.Compute(Definition) != DefinitionHash) throw new InvalidDataException("The pinned online exam definition changed.");
            }
            else Verify("testing-catalog.json", CatalogPath);
        }
        internal void VerifyPackage(ProjectPackage package)
        {
            if (IsOnline)
            {
                string root = Path.GetFullPath(package.ProjectFolderPath);
                CheckPath(root);
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                var audited = ContentHashes.Where(p => p.Key.StartsWith(package.Meta.ProjectId + "/", StringComparison.Ordinal)).ToArray();
                if (files.Length != audited.Length) throw new InvalidDataException("Pinned online package file set changed.");
                foreach (string file in files)
                    Verify(package.Meta.ProjectId + "/" + file.Substring(root.Length + 1).Replace('\\', '/'), file);
                return;
            }
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
        private readonly Func<DateTime> utcNow;
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
        public TestingSessionStateStore(string statePath = null, Func<DateTime> utcNow = null)
        {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            StatePath = Path.GetFullPath(statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MosWord2019", "testing-session.json"));
        }
        public PreparedTestingSession PrepareOnline(OnlinePackageSession online, IEnumerable<ProjectPackage> packages, string language, string machineId = null)
        {
            if (online == null || online.Manifest.Mode != "Testing" || online.ExamDefinition == null ||
                online.Manifest.SessionId == Guid.Empty || ExamDefinitionFingerprint.Compute(online.ExamDefinition) != online.Manifest.DefinitionHash)
                throw new InvalidDataException("A verified online Testing manifest is required.");
            if (language != "en" && language != "vi") throw new ArgumentException("Testing language must be en or vi.");
            lock (gate)
            {
                var available = packages.ToList();
                string id = online.Manifest.SessionId.ToString("N");
                var versions = JObject.FromObject(online.Manifest.Packages.ToDictionary(p => p.Code,
                    p => new { id = p.Id.ToString("D"), version = p.Version, sha256 = p.Sha256, zipSha256 = p.ZipSha256 }));
                if (File.Exists(StatePath))
                {
                    var prior = ReadEnvelope();
                    if ((string)prior["status"] == "Completed") ArchiveCompleted();
                    else
                    {
                        if ((string)prior["sessionId"] != id || (string)prior["onlineDefinitionHash"] != online.Manifest.DefinitionHash ||
                            !JToken.DeepEquals(prior["onlinePackages"], versions)) throw new InvalidDataException("Server resume does not match the pinned Testing session.");
                        var pinned = ReadPrepared(prior, null, available);
                        prior["recoveryPackages"] = JArray.FromObject(available); Write(prior, true);
                        return new PreparedTestingSession(pinned.SessionId, pinned.Language, pinned.Definition, null,
                            pinned.ContentHashes.ToDictionary(p => p.Key, p => p.Value), pinned.DefinitionHash, versions, JArray.FromObject(available));
                    }
                }
                var hashes = new Dictionary<string, string>();
                foreach (var project in online.ExamDefinition.Projects)
                {
                    var source = available.Single(p => p.Meta.ProjectId == project.ProjectId);
                    string expected = Path.Combine(online.ProjectsRootPath, project.ProjectId);
                    if (!string.Equals(Path.GetFullPath(source.ProjectFolderPath), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Online package is outside its verified cache.");
                    var reloaded = new ProjectLoader().Load(expected, language);
                    if (reloaded == null || !JToken.DeepEquals(JToken.FromObject(source.Tasks), JToken.FromObject(reloaded.Tasks)) ||
                        project.TaskIds.Any(t => !reloaded.Tasks.Any(p => p.TaskId == t && p.InstructionKey == project.InstructionKeys[t])))
                        throw new InvalidDataException("Online tasks do not match the signed definition.");
                    foreach (string file in Directory.GetFiles(expected, "*", SearchOption.AllDirectories))
                        hashes.Add(project.ProjectId + "/" + file.Substring(expected.Length + 1).Replace('\\', '/'), PreparedTestingSession.Hash(file));
                }
                var prepared = new PreparedTestingSession(id, language, online.ExamDefinition, null, hashes,
                    online.Manifest.DefinitionHash, versions, JArray.FromObject(available));
                SaveCore(prepared, null, null, new JObject { ["sessionId"] = id, ["onlineMachineId"] = machineId });
                return prepared;
            }
        }
        public TestingRecoveryRecord LoadOnline(OnlinePackageSession online, IEnumerable<ProjectPackage> packages)
        {
            PrepareOnline(online, packages, "en");
            return Load(null, packages);
        }
        public IReadOnlyList<ProjectPackage> ReadOnlineRecoveryPackages()
        {
            lock (gate)
            {
                var data = ReadEnvelope();
                if (data["onlineDefinitionHash"]?.Type != JTokenType.String || !(data["recoveryPackages"] is JArray))
                    throw new InvalidDataException("No pinned online Testing recovery metadata exists.");
                return data["recoveryPackages"].ToObject<List<ProjectPackage>>().AsReadOnly();
            }
        }
        public TestingRecoveryRecord LoadOnlineSaved() => Load(null, ReadOnlineRecoveryPackages());
        public void DiscardPreparedOnline(Guid sessionId)
        {
            lock (gate)
            {
                if (!File.Exists(StatePath)) return;
                var data = ReadEnvelope();
                if (data["onlineDefinitionHash"]?.Type != JTokenType.String ||
                    (string)data["sessionId"] != sessionId.ToString("N") || (string)data["status"] != "Prepared")
                    throw new InvalidOperationException("Only this untouched online Testing preparation may be discarded.");
                PreparedTestingSession.CheckPath(StatePath); File.Delete(StatePath);
            }
        }
        public string PendingOnlineStatus
        {
            get { lock (gate) { if (!File.Exists(StatePath)) return null; var data = ReadEnvelope();
                return data["onlineDefinitionHash"]?.Type == JTokenType.String ? (string)data["status"] : null; } }
        }
        public bool HasOnlineState => PendingOnlineSessionId.HasValue;
        public Guid? ReadOnlineSessionId() => PendingOnlineSessionId;
        public IReadOnlyList<ProjectPackage> RecoveryPackages => ReadOnlineRecoveryPackages();
        public Guid? PendingOnlineSessionId
        {
            get { lock (gate) { if (!File.Exists(StatePath)) return null; var data = ReadEnvelope();
                return data["onlineDefinitionHash"]?.Type == JTokenType.String ? (Guid?)Guid.Parse((string)data["sessionId"]) : null; } }
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
        private void SaveCore(PreparedTestingSession prepared, TestingSession session, DateTime? lastSaveUtc, JObject initial = null)
        {
            if (prepared == null || (session != null && (session.SessionId != prepared.SessionId || session.Definition != prepared.Definition)))
                throw new InvalidDataException("Testing snapshot does not match its prepared definition.");
            PreparedTestingSession.CheckPath(StatePath);
            var prior = initial ?? JObject.Parse(File.ReadAllText(StatePath));
            if ((string)prior["sessionId"] != prepared.SessionId) throw new InvalidDataException("Testing state belongs to another session.");
            if (lastSaveUtc.HasValue && lastSaveUtc.Value.Kind != DateTimeKind.Utc) throw new ArgumentException("Last save time must be UTC.");
            var snapshot = session?.CaptureState();
            var data = new JObject
            {
                ["version"] = 1, ["sessionId"] = prepared.SessionId, ["seed"] = prepared.Definition.Seed,
                ["machine"] = Environment.MachineName, ["language"] = prepared.Language,
                ["onlineMachineId"] = prior["onlineMachineId"]?.DeepClone(),
                ["onlineDefinitionHash"] = prepared.DefinitionHash, ["onlinePackages"] = prepared.OnlinePackages?.DeepClone(),
                ["recoveryPackages"] = prepared.RecoveryPackages?.DeepClone(),
                ["definition"] = JObject.FromObject(prepared.Definition), ["contentHashes"] = JObject.FromObject(prepared.ContentHashes),
                ["startedUtc"] = snapshot?.StartedUtc.HasValue == true ? new JValue(snapshot.StartedUtc.Value) : JValue.CreateNull(),
                ["completedUtc"] = snapshot?.CompletedUtc.HasValue == true ? new JValue(snapshot.CompletedUtc.Value) : JValue.CreateNull(),
                ["offlinePeriods"] = JArray.FromObject(snapshot?.OfflinePeriods ?? new TestingOfflinePeriod[0]),
                ["historyIncomplete"] = snapshot?.HistoryIncomplete ?? false,
                ["lastHeartbeatUtc"] = utcNow(), ["lastSaveUtc"] = lastSaveUtc.HasValue ? new JValue(lastSaveUtc.Value) : JValue.CreateNull(),
                ["clientWallUtc"] = prepared.IsOnline ? new JValue(DateTime.UtcNow) : JValue.CreateNull(),
                ["finalizeReason"] = snapshot?.FinalizeReason.ToString() ?? "None",
                ["clockRollbackDetected"] = (bool?)prior["clockRollbackDetected"] == true ||
                    (prepared.IsOnline && prior["clientWallUtc"]?.Type == JTokenType.Date && (DateTime)prior["clientWallUtc"] > DateTime.UtcNow),
                ["ownedWord"] = prior["ownedWord"]?.DeepClone() ?? JValue.CreateNull(),
                ["projectContexts"] = prior["projectContexts"]?.DeepClone() ?? new JObject(),
                ["offlineElapsedTicks"] = snapshot?.OfflineElapsed.Ticks ?? 0,
                ["elapsedTicks"] = snapshot?.Elapsed.Ticks ?? 0, ["currentIndex"] = snapshot?.CurrentIndex ?? 0,
                ["status"] = snapshot == null ? "Prepared" : snapshot.Status.ToString(),
                ["projectStates"] = JArray.FromObject(snapshot?.ProjectStates ?? Enumerable.Repeat(TestingProjectState.NotStarted, 7).ToArray()),
                ["lockedResults"] = JArray.FromObject(snapshot?.LockedResults ?? new ExamTaskGrade[0]),
                ["result"] = snapshot?.Result == null ? JValue.CreateNull() : JToken.FromObject(snapshot.Result)
            };
            Write(data, initial == null);
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
