using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MosWord2019.Projects.OnlinePackages
{
    public sealed class OnlinePackageManifest
    {
        public Guid SessionId { get; internal set; }
        public string Mode { get; internal set; }
        public string ExamSeed { get; internal set; }
        public string DefinitionHash { get; internal set; }
        public IReadOnlyList<OnlinePackageDescriptor> Packages { get; internal set; }
        public ExamDefinition ExamDefinition { get; internal set; }
    }
    public sealed class OnlinePackageDescriptor
    {
        public Guid Id { get; internal set; }
        public string Code { get; internal set; }
        public string Version { get; internal set; }
        public string Sha256 { get; internal set; }
        public string ZipSha256 { get; internal set; }
        public long SizeBytes { get; internal set; }
        public string DownloadPath { get; internal set; }
        internal byte[] Key;
    }
    public sealed class OnlinePackageSession : IDisposable
    {
        private readonly PackageCacheLedger ledger;
        private bool released;
        internal OnlinePackageSession(OnlinePackageManifest manifest, string root, PackageCacheLedger ledger)
        { Manifest = manifest; ProjectsRootPath = root; this.ledger = ledger; }
        public OnlinePackageManifest Manifest { get; }
        public string ProjectsRootPath { get; }
        public ExamDefinition ExamDefinition => Manifest.ExamDefinition;
        public void Dispose()
        {
            if (released) return;
            released = true;
            try { ledger.Cleanup(); }
            finally { ledger.Dispose(); }
        }
    }
    public sealed class OnlinePackageCache
    {
        public const int MaximumBlobBytes = 128 * 1024 * 1024;
        private const long MaximumExpandedBytes = 512L * 1024 * 1024;
        private static readonly Regex CodePattern = new Regex(@"\AWord2019_P[0-9]{2,4}\z", RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
        private readonly IPackageTransport transport;
        private readonly SessionRecoveryJournal journal;
        private readonly byte[] publicKey;
        private readonly string packagesRoot;
        public OnlinePackageCache(IPackageTransport transport, SessionRecoveryJournal journal, byte[] trustedPublicKey = null, string packagesRoot = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
            publicKey = (byte[])(trustedPublicKey ?? Convert.FromBase64String(PackageTrust.PublicKey)).Clone();
            if (publicKey.Length != 32) throw new ArgumentException("An Ed25519 public key is required.");
            this.packagesRoot = Path.GetFullPath(packagesRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosWord2019", "Packages"));
        }
        public async Task<OnlinePackageSession> LoadAsync(Guid sessionId, string mode, IEnumerable<string> requestedCodes, string sessionToken, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sessionId == Guid.Empty || (mode != "Training" && mode != "Testing")) throw new ArgumentException("Invalid session context.");
            var requested = (requestedCodes ?? (mode == "Testing" ? Enumerable.Empty<string>() : throw new ArgumentNullException(nameof(requestedCodes)))).ToArray();
            if ((requested.Length == 0 && mode != "Testing") || requested.Length > 100 || requested.Any(c => c == null || !CodePattern.IsMatch(c)) || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
                throw new ArgumentException("Explicit distinct package codes are required.");
            var manifest = VerifyManifest(await transport.GetManifest(sessionToken, cancellationToken).ConfigureAwait(false), sessionId, mode);
            try
            {
                if (mode == "Testing")
                {
                    if (requested.Length == 0) requested = manifest.ExamDefinition.Projects.Select(p => p.ProjectId).ToArray();
                    if (!new HashSet<string>(manifest.ExamDefinition.Projects.Select(p => p.ProjectId), StringComparer.Ordinal).SetEquals(requested)) throw new InvalidDataException("Requested packages do not match the composed exam.");
                    await transport.ReportDefinition(manifest.DefinitionHash, sessionToken, cancellationToken).ConfigureAwait(false);
                }
                var selected = requested.Select(code => manifest.Packages.SingleOrDefault(p => p.Code == code) ?? throw new InvalidDataException("Requested package is absent from manifest.")).ToArray();
                RecoverIncompletePackages();
                string root = Path.Combine(packagesRoot, sessionId.ToString("N"), Guid.NewGuid().ToString("N"));
                var ledger = new PackageCacheLedger(root, journal);
                try
                {
                    foreach (var package in selected)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte[] blob = await transport.Download(package.DownloadPath, sessionToken, cancellationToken).ConfigureAwait(false);
                        byte[] zip = Decrypt(package, blob);
                        try { Extract(zip, Path.Combine(root, package.Code), ledger, cancellationToken); }
                        finally { Array.Clear(zip, 0, zip.Length); }
                    }
                    return new OnlinePackageSession(manifest, root, ledger);
                }
                catch { try { ledger.Cleanup(); } finally { ledger.Dispose(); } throw; }
            }
            finally { foreach (var package in manifest.Packages) Array.Clear(package.Key, 0, package.Key.Length); }
        }
        public void RecoverIncompletePackages() { PackageCacheLedger.Recover(packagesRoot, journal); }
        public static void RecoverIncompletePackages(SessionRecoveryJournal journal, string packagesRoot = null)
        {
            if (journal == null) throw new ArgumentNullException(nameof(journal));
            PackageCacheLedger.Recover(Path.GetFullPath(packagesRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosWord2019", "Packages")), journal);
        }
        public OnlinePackageManifest VerifyManifest(byte[] envelopeBytes, Guid sessionId, string mode)
        {
            if (envelopeBytes == null || envelopeBytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Invalid manifest envelope.");
            var envelope = Parse(envelopeBytes);
            if (envelope.Properties().Count() != 2) throw new InvalidDataException("Invalid manifest envelope.");
            byte[] payload = Decode(String(envelope, "payload"), 1024 * 1024);
            byte[] signature = Decode(String(envelope, "signature"), 64);
            if (signature.Length != 64) throw new InvalidDataException("Invalid manifest signature.");
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(payload, 0, payload.Length);
            if (!verifier.VerifySignature(signature)) throw new InvalidDataException("Invalid manifest signature.");
            // No payload field is parsed or trusted until its exact UTF-8 bytes have been verified.
            var value = Parse(payload);
            Guid actualSession;
            if (value["schemaVersion"]?.Type != JTokenType.Integer || value["schemaVersion"].ToString() != "2" ||
                !Guid.TryParse(String(value, "sessionId"), out actualSession) || actualSession == Guid.Empty || actualSession != sessionId || String(value, "mode") != mode || (mode != "Training" && mode != "Testing"))
                throw new InvalidDataException("Manifest session context does not match.");
            var array = value["packages"] as JArray;
            if (array == null || array.Count == 0 || array.Count > 100) throw new InvalidDataException("Invalid package manifest.");
            var packages = new List<OnlinePackageDescriptor>();
            foreach (var token in array)
            {
                var item = token as JObject;
                Guid id;
                if (item == null || !Guid.TryParse(String(item, "id"), out id) || id == Guid.Empty) throw new InvalidDataException("Invalid package identifier.");
                string code = String(item, "code"), version = String(item, "version"), sha = String(item, "sha256"), zipSha = String(item, "zipSha256");
                long size;
                if (!CodePattern.IsMatch(code) || !HashPattern.IsMatch(version) || !HashPattern.IsMatch(sha) || !HashPattern.IsMatch(zipSha) || version != zipSha ||
                    item["sizeBytes"]?.Type != JTokenType.Integer || !long.TryParse(item["sizeBytes"].ToString(), out size) || size < 36 || size > MaximumBlobBytes ||
                    String(item, "downloadPath") != "/api/packages/" + id.ToString("D") + "/download") throw new InvalidDataException("Invalid package descriptor.");
                byte[] key = Decode(String(item, "key"), 32);
                if (key.Length != 32 || packages.Any(p => p.Id == id || p.Code == code)) throw new InvalidDataException("Ambiguous package descriptor.");
                packages.Add(new OnlinePackageDescriptor { Id = id, Code = code, Version = version, Sha256 = sha, ZipSha256 = zipSha, SizeBytes = size, DownloadPath = String(item, "downloadPath"), Key = key });
            }
            string seed = Optional(value, "examSeed"), definition = Optional(value, "definitionHash");
            if ((seed != null && (seed.Length == 0 || seed.Length > 256)) || (definition != null && !HashPattern.IsMatch(definition))) throw new InvalidDataException("Invalid exam manifest context.");
            ExamDefinition exam = null;
            if (mode == "Testing")
            {
                long numericSeed;
                if (seed == null || definition == null || !long.TryParse(seed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out numericSeed) ||
                    value["catalog"] != null || !(value["examDefinition"] is JObject) || !(value["examPool"] is JArray))
                    throw new InvalidDataException("Testing manifest requires the signed server definition.");
                exam = ReadDefinition((JObject)value["examDefinition"], numericSeed);
                if (ExamDefinitionFingerprint.Compute(exam) != definition) throw new InvalidDataException("Signed exam definition hash does not match.");
                if (packages.Count != 7 || !new HashSet<string>(exam.Projects.Select(p => p.ProjectId), StringComparer.Ordinal).SetEquals(packages.Select(p => p.Code)))
                    throw new InvalidDataException("Manifest packages do not match the signed exam.");
                var pool = ((JArray)value["examPool"]).ToObject<List<ProjectPackage>>();
                if (pool.Count != 7 || pool.Any(p => p == null || p.Meta == null || p.Tasks == null) ||
                    pool.Select(p => p.Meta.ProjectId).Distinct(StringComparer.Ordinal).Count() != 7 ||
                    !new HashSet<string>(exam.Projects.Select(p => p.ProjectId), StringComparer.Ordinal).SetEquals(pool.Select(p => p.Meta.ProjectId)))
                    throw new InvalidDataException("Invalid signed exam metadata.");
                foreach (var project in exam.Projects)
                {
                    var tasks = pool.Single(p => p.Meta.ProjectId == project.ProjectId).Tasks;
                    if (tasks.Count != project.TaskIds.Count || tasks.Any(t => t == null || !project.TaskIds.Contains(t.TaskId) || t.InstructionKey != project.InstructionKeys[t.TaskId]) ||
                        tasks.Select(t => t.TaskId).Distinct(StringComparer.Ordinal).Count() != tasks.Count)
                        throw new InvalidDataException("Signed task metadata does not match the exam definition.");
                }
            }
            return new OnlinePackageManifest { SessionId = actualSession, Mode = mode, ExamSeed = seed, DefinitionHash = definition, Packages = packages.AsReadOnly(), ExamDefinition = exam };
        }
        private static ExamDefinition ReadDefinition(JObject value, long seed)
        {
            long actualSeed;
            var projects = value["projects"] as JArray;
            if (value.Properties().Count() != 3 || value["seed"]?.Type != JTokenType.Integer ||
                !long.TryParse(value["seed"].ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out actualSeed) || actualSeed != seed ||
                value["n"]?.Type != JTokenType.Integer || projects == null || projects.Count != 7)
                throw new InvalidDataException("Invalid signed exam definition.");
            var selected = new List<ExamProjectDefinition>();
            foreach (var token in projects)
            {
                var project = token as JObject;
                if (project == null || project.Properties().Count() != 3) throw new InvalidDataException("Invalid signed exam project.");
                string id = String(project, "projectId");
                var ids = project["taskIds"] as JArray;
                var keys = project["instructionKeys"] as JObject;
                if (!CodePattern.IsMatch(id) || ids == null || ids.Count < 4 || ids.Count > 6 || keys == null || keys.Properties().Count() != ids.Count)
                    throw new InvalidDataException("Invalid signed exam tasks.");
                var tasks = new List<TaskDefinition>();
                foreach (var task in ids)
                {
                    if (task.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)task) || ((string)task).Length > 64)
                        throw new InvalidDataException("Invalid signed task identifier.");
                    string taskId = (string)task;
                    string key = String(keys, taskId);
                    if (string.IsNullOrWhiteSpace(key) || key.Length > 256) throw new InvalidDataException("Invalid signed instruction key.");
                    tasks.Add(new TaskDefinition { TaskId = taskId, InstructionKey = key });
                }
                selected.Add(new ExamProjectDefinition(id, tasks));
            }
            var definition = new ExamDefinition(seed, selected);
            if (value["n"].ToString() != definition.N.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException("Signed task count does not match.");
            return definition;
        }
        private static byte[] Decrypt(OnlinePackageDescriptor package, byte[] blob)
        {
            if (blob == null || blob.LongLength != package.SizeBytes || Hash(blob) != package.Sha256 || Encoding.ASCII.GetString(blob, 0, 8) != "MWPKG001") throw new InvalidDataException("Invalid encrypted package.");
            var nonce = new byte[12]; Buffer.BlockCopy(blob, 8, nonce, 0, 12);
            var input = new byte[blob.Length - 36 + 16];
            Buffer.BlockCopy(blob, 36, input, 0, blob.Length - 36);
            Buffer.BlockCopy(blob, 20, input, blob.Length - 36, 16);
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(false, new AeadParameters(new KeyParameter(package.Key), 128, nonce, Encoding.UTF8.GetBytes("MosWord2019.Package.v1|" + package.Code + "|" + package.Version)));
            var zip = new byte[cipher.GetOutputSize(input.Length)];
            try
            {
                int length = cipher.ProcessBytes(input, 0, input.Length, zip, 0);
                length += cipher.DoFinal(zip, length);
                if (length != zip.Length || Hash(zip) != package.ZipSha256) throw new InvalidDataException("Invalid decrypted package.");
                return zip;
            }
            catch { Array.Clear(zip, 0, zip.Length); throw; }
            finally { Array.Clear(input, 0, input.Length); }
        }
        private void Extract(byte[] zip, string root, PackageCacheLedger ledger, CancellationToken ct)
        {
            using (var memory = new MemoryStream(zip, false))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count == 0 || archive.Entries.Count > 4096) throw new InvalidDataException("Invalid package archive size.");
                long total = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    ValidateEntry(entry, names);
                    if (entry.Length > 128L * 1024 * 1024 || entry.Length > MaximumExpandedBytes - total || (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 500)) throw new InvalidDataException("Package expansion limit exceeded.");
                    total += entry.Length;
                }
                ledger.CreateDirectory(root);
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { ledger.CreateDirectory(path); continue; }
                    ledger.CreateDirectory(Path.GetDirectoryName(path));
                    RejectReparse(path);
                        using (var input = entry.Open())
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                        {
                            ledger.ClaimEmptyFile(path, output);
                            var buffer = new byte[81920]; long written = 0; int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                written += count;
                                if (written > entry.Length) throw new InvalidDataException("Invalid expanded package length.");
                                output.Write(buffer, 0, count);
                            }
                            if (written != entry.Length) throw new InvalidDataException("Invalid expanded package length.");
                            ledger.CompleteFile(path, output);
                        }
                        ledger.RegisterCompleted(path);
                }
            }
        }
        private static void ValidateEntry(ZipArchiveEntry entry, HashSet<string> names)
        {
            string name = entry.FullName;
            if (name.Length == 0 || name.Length > 240 || name.Contains("\\") || name.Contains(":") || name.StartsWith("/", StringComparison.Ordinal) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe package entry.");
            string normalized = name.TrimEnd('/');
            if (!names.Add(normalized) || normalized.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal) || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) throw new InvalidDataException("Unsafe or duplicate package entry.");
        }
        internal static void CreatePrivateDirectory(string path)
        {
            RejectReparse(path);
            if (Directory.Exists(path)) return;
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) CreatePrivateDirectory(parent);
            var security = new DirectorySecurity();
            var user = WindowsIdentity.GetCurrent().User;
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(path, security);
            RejectReparse(path);
        }
        internal static void RejectReparse(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected package path.");
        }
        private static JObject Parse(byte[] bytes)
        { return JObject.Parse(new UTF8Encoding(false, true).GetString(bytes), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
        private static string String(JObject value, string key)
        { if (value[key]?.Type != JTokenType.String) throw new InvalidDataException("Invalid manifest field: " + key); return (string)value[key]; }
        private static string Optional(JObject value, string key)
        { return value[key] == null || value[key].Type == JTokenType.Null ? null : String(value, key); }
        private static byte[] Decode(string value, int limit)
        { if (value.Length > ((limit + 2L) / 3) * 4) throw new InvalidDataException("Manifest field exceeds limit."); return Convert.FromBase64String(value); }
        private static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
