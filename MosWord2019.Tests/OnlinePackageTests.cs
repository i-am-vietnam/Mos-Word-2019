using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Projects;
using MosWord2019.Projects.OnlinePackages;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class OnlinePackageTests
    {
        [TestMethod]
        public async Task NativeDotNetAesGcmFixedVectorDecryptsInNet472Client()
        {
            // Fixed vector generated with PowerShell/.NET AesGcm, not BouncyCastle encryption.
            using (var workspace = new TestWorkspace())
            {
                const string blobText = "TVdQS0cwMDEAAQIDBAUGBwgJCguejaCrWNeD4RCyRig2YbVWF0nVH9HlwhuFQZeLkLl/cRfIoDTwe0B8OGfshR1pbdd1cYCW3K58Uj+ItsHD0lhM448V+qyDaxYU3gfTN6vYpLz2CrP/eCJhHFRVkeyPXqM5iN1iYmNTaPX9nsxZoL6RX+Z+5qRB1xqeHDeySHUhf7pnYqvjxSWaTf9Xze+DGm+VOHOp2oeAVg7hSKE920zDNsPjNTVfk0qMX+g=";
                const string zipHash = "fedf5f1c7103ce2e8d2fc23fc22a20097c7568ee2bb7fcf5088e1e384e61bca1";
                var f = new Fixture(); var id = Guid.NewGuid(); string path = "/api/packages/" + id.ToString("D") + "/download";
                byte[] blob = Convert.FromBase64String(blobText); f.Blobs[path] = blob;
                ((JArray)f.Payload["packages"]).Add(new JObject { ["id"] = id, ["code"] = "Word2019_P01", ["version"] = zipHash, ["zipSha256"] = zipHash, ["sha256"] = "0e1fab9d2880aa79bfebf551f62dfe46264b0199d0703e9b1d90d9a60e5757ad", ["sizeBytes"] = blob.Length, ["key"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", ["downloadPath"] = path });
                var journal = new SessionRecoveryJournal(Path.Combine(workspace.Folder, "journal.json"), _ => { });
                using (var session = await f.Cache(journal, workspace.Folder).LoadAsync(f.Session, "Training", new[] { "Word2019_P01" }, "token"))
                    Assert.AreEqual("native AES-GCM interoperability", File.ReadAllText(Path.Combine(session.ProjectsRootPath, "Word2019_P01", "meta.json")));
            }
        }

        [TestMethod]
        public async Task TransportPublishesRefreshedTokenAfterEverySuccessfulReply()
        {
            using (var client = new HttpClient(new ReplyHandler("refreshed-token")))
            using (var transport = new HttpPackageTransport(new Uri("https://example.invalid/"), client))
            {
                var tokens = new List<string>(); transport.SessionTokenRefreshed += tokens.Add;
                await transport.GetManifest("token", CancellationToken.None);
                await transport.Download("/api/packages/" + Guid.NewGuid().ToString("D") + "/download", "token", CancellationToken.None);
                await transport.ReportDefinition(new string('a', 64), "token", CancellationToken.None);
                Assert.AreEqual(3, tokens.Count);
                Assert.IsTrue(tokens.All(t => t == "refreshed-token"));
            }
        }
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(4097)]
        public async Task TransportRejectsEmptyOrOversizedRefreshToken(int length)
        {
            using (var client = new HttpClient(new ReplyHandler(new string('x', length))))
            using (var transport = new HttpPackageTransport(new Uri("https://example.invalid/"), client))
                await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => { await transport.GetManifest("token", CancellationToken.None); });
        }
        private sealed class ReplyHandler : HttpMessageHandler
        {
            private readonly string token;
            internal ReplyHandler(string token) { this.token = token; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(new byte[0]) };
                response.Headers.TryAddWithoutValidation("Session-Token", token);
                return Task.FromResult(response);
            }
        }

        [TestMethod]
        public async Task SignedManifestDecryptsOnlyRequestedPackageAndCrashRecoveryDeletesExactFiles()
        {
            using (var workspace = new TestWorkspace())
            {
                var fixture = new Fixture();
                fixture.Add("Word2019_P01", "assets/example.txt", "Exact supplied content\r\nTiếng Việt");
                fixture.Add("Word2019_P02", "starter.docx", "not requested");
                string journalPath = Path.Combine(workspace.Folder, "journal.json");
                var journal = new SessionRecoveryJournal(journalPath, _ => { });
                var cache = fixture.Cache(journal, workspace.Folder);
                var session = await cache.LoadAsync(fixture.Session, "Training", new[] { "Word2019_P01" }, "session-token");
                string file = Path.Combine(session.ProjectsRootPath, "Word2019_P01", "assets", "example.txt");
                Assert.AreEqual("Exact supplied content\r\nTiếng Việt", File.ReadAllText(file));
                Assert.AreEqual(1, fixture.Downloads.Count);
                Assert.AreEqual(1, journal.Count);
                Assert.IsFalse(Directory.Exists(Path.Combine(session.ProjectsRootPath, "Word2019_P02")));
                var security = Directory.GetAccessControl(session.ProjectsRootPath);
                Assert.IsTrue(security.AreAccessRulesProtected);
                var current = WindowsIdentity.GetCurrent().User;
                Assert.IsTrue(security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().All(rule => rule.IdentityReference.Equals(current)));
                journal.Recover();
                Assert.IsTrue(File.Exists(file), "Active online session must be preserved.");
                session.Dispose();
                Assert.IsFalse(Directory.Exists(session.ProjectsRootPath), "Normal session end removes owned package directories immediately.");
                new SessionRecoveryJournal(journalPath, _ => { }).Recover();
                Assert.IsFalse(File.Exists(file));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void DurableWriteAheadLedgerRecoversCrashBeforeGeneralJournalTrack(bool completed)
        {
            using (var workspace = new TestWorkspace())
            {
                string cacheRoot = Path.Combine(workspace.Folder, "cache");
                string root = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
                var journal = new SessionRecoveryJournal(Path.Combine(workspace.Folder, "journal.json"), _ => { });
                string path = Path.Combine(root, "Word2019_P01", "starter.docx");
                using (var ledger = new PackageCacheLedger(root, journal))
                {
                    ledger.CreateDirectory(Path.GetDirectoryName(path));
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        ledger.ClaimEmptyFile(path, stream);
                        byte[] bytes = Encoding.UTF8.GetBytes("partial plaintext before journal.Track");
                        stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                        if (completed) ledger.CompleteFile(path, stream);
                    }
                    // Disposing only the lease models process loss at either write/Track boundary.
                }
                Assert.AreEqual(0, journal.Count);
                PackageCacheLedger.Recover(cacheRoot, journal);
                Assert.IsFalse(File.Exists(path));
                Assert.IsFalse(Directory.Exists(root));
            }
        }

        [TestMethod]
        public void PackageLedgerPreservesReplacementFileWithSameBytes()
        {
            using (var workspace = new TestWorkspace())
            {
                string cacheRoot = Path.Combine(workspace.Folder, "cache");
                string root = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
                var journal = new SessionRecoveryJournal(Path.Combine(workspace.Folder, "journal.json"), _ => { });
                string path = Path.Combine(root, "meta.json");
                using (var ledger = new PackageCacheLedger(root, journal))
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    { ledger.ClaimEmptyFile(path, stream); stream.WriteByte(42); ledger.CompleteFile(path, stream); }
                }
                File.Move(path, Path.Combine(workspace.Folder, "original.dat"));
                File.WriteAllBytes(path, new byte[] { 42 });
                PackageCacheLedger.Recover(cacheRoot, journal);
                Assert.IsTrue(File.Exists(path));
                Assert.AreEqual(42, File.ReadAllBytes(path)[0]);
            }
        }

        [TestMethod]
        public async Task ConcurrentHandlesForSameOnlineSessionKeepIndependentRecoveryProtection()
        {
            using (var workspace = new TestWorkspace())
            {
                var f = new Fixture(); f.Add("Word2019_P01", "meta.json", "first"); f.Add("Word2019_P02", "meta.json", "second");
                var journal = new SessionRecoveryJournal(Path.Combine(workspace.Folder, "journal.json"), _ => { });
                var cache = f.Cache(journal, workspace.Folder);
                using (var first = await cache.LoadAsync(f.Session, "Training", new[] { "Word2019_P01" }, "token"))
                using (var second = await cache.LoadAsync(f.Session, "Training", new[] { "Word2019_P02" }, "token"))
                {
                    Assert.IsTrue(Directory.Exists(first.ProjectsRootPath));
                    first.Dispose(); journal.Recover();
                    Assert.IsFalse(Directory.Exists(first.ProjectsRootPath));
                    Assert.IsTrue(File.Exists(Path.Combine(second.ProjectsRootPath, "Word2019_P02", "meta.json")));
                }
                Assert.AreEqual(0, journal.Count);
            }
        }

        [TestMethod]
        public async Task SignedFullPoolComposesExistingExamReportsHashAndDownloadsOnlySelectedSeven()
        {
            using (var workspace = new TestWorkspace())
            {
                var f = new Fixture();
                string projectsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects");
                var pool = new ProjectLoader().LoadAll(projectsRoot, "en");
                var catalog = new TestingCatalogLoader().Load(Path.Combine(projectsRoot, "testing-catalog.json"), pool);
                Assert.AreEqual(28, pool.Count);
                const long seed = 37612;
                var definition = ExamComposer.Compose(catalog, pool, seed);
                foreach (var project in definition.Projects) f.Add(project.ProjectId, "meta.json", "fixture");
                f.Payload["mode"] = "Testing";
                f.Payload["examSeed"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                f.Payload["definitionHash"] = ExamDefinitionFingerprint.Compute(definition);
                f.Payload["catalog"] = JArray.FromObject(catalog);
                f.Payload["examPool"] = JArray.FromObject(pool.Select(p => new { meta = p.Meta, tasks = p.Tasks }));
                var journal = new SessionRecoveryJournal(Path.Combine(workspace.Folder, "journal.json"), _ => { });
                using (var session = await f.Cache(journal, workspace.Folder).LoadAsync(f.Session, "Testing", null, "token"))
                {
                    Assert.AreEqual(7, f.Downloads.Count);
                    Assert.AreEqual(1, f.Reports.Count);
                    Assert.AreEqual(ExamDefinitionFingerprint.Compute(definition), f.Reports.Single());
                    CollectionAssert.AreEqual(definition.Projects.Select(p => p.ProjectId).ToArray(), session.ExamDefinition.Projects.Select(p => p.ProjectId).ToArray());
                    Assert.AreEqual(definition.N, session.ExamDefinition.N);
                }
                f.Payload["definitionHash"] = new string('a', 64);
                await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => { await f.Cache(journal, workspace.Folder).LoadAsync(f.Session, "Testing", null, "token"); });
                Assert.AreEqual(7, f.Downloads.Count, "Definition mismatch must fail before any further download.");
                Assert.AreEqual(1, f.Reports.Count);
            }
        }

        [TestMethod]
        public async Task RecoveryDeletesModifiedOwnedPackageFile()
        {
            using (var workspace = new TestWorkspace())
            {
                var fixture = new Fixture(); fixture.Add("Word2019_P01", "meta.json", "original");
                var journal = new SessionRecoveryJournal(Path.Combine(workspace.Folder, "journal.json"), _ => { });
                using (var session = await fixture.Cache(journal, workspace.Folder).LoadAsync(fixture.Session, "Training", new[] { "Word2019_P01" }, "token"))
                {
                    string path = Path.Combine(session.ProjectsRootPath, "Word2019_P01", "meta.json");
                    File.WriteAllText(path, "changed");
                    session.Dispose(); journal.Recover();
                    Assert.IsFalse(File.Exists(path));
                }
            }
        }

        [DataTestMethod]
        [DataRow("signature")]
        [DataRow("session")]
        [DataRow("mode")]
        [DataRow("code")]
        [DataRow("id")]
        [DataRow("path")]
        [DataRow("size")]
        [DataRow("version")]
        [DataRow("key")]
        [DataRow("duplicate")]
        public async Task UntrustedOrInvalidManifestNeverDownloads(string fault)
        {
            using (var workspace = new TestWorkspace())
            {
                var f = new Fixture(); f.Add("Word2019_P01", "meta.json", "data");
                var item = (JObject)((JArray)f.Payload["packages"])[0];
                switch (fault)
                {
                    case "signature": f.BadSignature = true; break;
                    case "session": f.Payload["sessionId"] = Guid.NewGuid(); break;
                    case "mode": f.Payload["mode"] = "Testing"; break;
                    case "code": item["code"] = "../Word2019_P01"; break;
                    case "id": item["id"] = Guid.Empty; break;
                    case "path": item["downloadPath"] = "https://attacker.invalid/api/packages/x/download"; break;
                    case "size": item["sizeBytes"] = long.MaxValue; break;
                    case "version": item["version"] = new string('a', 64); break;
                    case "key": item["key"] = Convert.ToBase64String(new byte[31]); break;
                    case "duplicate": ((JArray)f.Payload["packages"]).Add(item.DeepClone()); break;
                }
                await Reject(f, workspace.Folder);
                Assert.AreEqual(0, f.Downloads.Count);
            }
        }

        [DataTestMethod]
        [DataRow("hash")]
        [DataRow("tag")]
        [DataRow("magic")]
        [DataRow("ziphash")]
        public async Task CorruptCiphertextOrAuthenticationNeverExtracts(string fault)
        {
            using (var workspace = new TestWorkspace())
            {
                var f = new Fixture(); f.Add("Word2019_P01", "meta.json", "data");
                var item = (JObject)((JArray)f.Payload["packages"])[0];
                var bytes = f.Blobs[(string)item["downloadPath"]];
                if (fault == "hash") bytes[bytes.Length - 1] ^= 1;
                if (fault == "tag") { bytes[20] ^= 1; item["sha256"] = Hash(bytes); }
                if (fault == "magic") { bytes[0] ^= 1; item["sha256"] = Hash(bytes); }
                if (fault == "ziphash") { item["zipSha256"] = item["version"] = new string('a', 64); }
                await Reject(f, workspace.Folder);
                Assert.AreEqual(0, Directory.GetFiles(Path.Combine(workspace.Folder, "cache"), "meta.json", SearchOption.AllDirectories).Length);
            }
        }

        [DataTestMethod]
        [DataRow("../escaped.txt")]
        [DataRow("/escaped.txt")]
        [DataRow("C:/escaped.txt")]
        [DataRow("assets\\..\\escaped.txt")]
        [DataRow("assets/../escaped.txt")]
        [DataRow("assets/file.txt:stream")]
        [DataRow("assets/file.txt.")]
        [DataRow("assets/CON.txt")]
        [DataRow("assets//file.txt")]
        public async Task ArchiveTraversalAndWindowsAliasesAreRejected(string entry)
        {
            using (var workspace = new TestWorkspace())
            {
                var f = new Fixture(); f.Add("Word2019_P01", entry, "data");
                await Reject(f, workspace.Folder);
                Assert.IsFalse(File.Exists(Path.Combine(workspace.Folder, "escaped.txt")));
            }
        }

        [DataTestMethod]
        [DataRow("duplicate")]
        [DataRow("symlink")]
        [DataRow("bomb")]
        public async Task AmbiguousLinksAndExpansionBombsAreRejected(string fault)
        {
            using (var workspace = new TestWorkspace())
            {
                var f = new Fixture();
                f.Add("Word2019_P01", "assets/file.txt", fault == "bomb" ? new string('x', 2 * 1024 * 1024) : "data", fault);
                await Reject(f, workspace.Folder);
            }
        }

        [TestMethod]
        public async Task MissingRequestedCodeDoesNotDownloadOrExtract()
        {
            using (var workspace = new TestWorkspace())
            {
                var f = new Fixture(); f.Add("Word2019_P02", "meta.json", "data");
                await Reject(f, workspace.Folder);
                Assert.AreEqual(0, f.Downloads.Count);
            }
        }
        private static async Task Reject(Fixture f, string root)
        {
            var journal = new SessionRecoveryJournal(Path.Combine(root, "journal.json"), _ => { });
            bool rejected = false;
            try { using (await f.Cache(journal, root).LoadAsync(f.Session, "Training", new[] { "Word2019_P01" }, "token")) { } }
            catch (Exception ex) when (ex is InvalidDataException || ex is Org.BouncyCastle.Crypto.InvalidCipherTextException || ex is FormatException) { rejected = true; }
            Assert.IsTrue(rejected, "Unsafe package must be rejected.");
            journal.Recover();
        }
        private static string Hash(byte[] value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        private sealed class Fixture : IPackageTransport
        {
            internal readonly Guid Session = Guid.NewGuid();
            internal readonly JObject Payload;
            internal readonly Dictionary<string, byte[]> Blobs = new Dictionary<string, byte[]>();
            internal readonly List<string> Downloads = new List<string>();
            internal readonly List<string> Reports = new List<string>();
            internal bool BadSignature;
            private readonly Ed25519PrivateKeyParameters key;
            internal Fixture()
            {
                var seed = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(seed);
                key = new Ed25519PrivateKeyParameters(seed, 0);
                Payload = new JObject { ["schemaVersion"] = 1, ["sessionId"] = Session, ["mode"] = "Training", ["packages"] = new JArray() };
            }
            internal OnlinePackageCache Cache(SessionRecoveryJournal journal, string root)
            { return new OnlinePackageCache(this, journal, key.GeneratePublicKey().GetEncoded(), Path.Combine(root, "cache")); }
            internal void Add(string code, string entryName, string content, string fault = null)
            {
                byte[] zip;
                using (var memory = new MemoryStream())
                {
                    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                    {
                        var entry = archive.CreateEntry(entryName);
                        if (fault == "symlink") entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                        using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(content);
                        if (fault == "duplicate") using (var writer = new StreamWriter(archive.CreateEntry("assets/FILE.txt").Open())) writer.Write("other");
                    }
                    zip = memory.ToArray();
                }
                byte[] aes = new byte[32], nonce = new byte[12];
                using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(aes); rng.GetBytes(nonce); }
                string version = Hash(zip);
                var cipher = new GcmBlockCipher(new AesEngine());
                cipher.Init(true, new AeadParameters(new KeyParameter(aes), 128, nonce, Encoding.UTF8.GetBytes("MosWord2019.Package.v1|" + code + "|" + version)));
                var encrypted = new byte[cipher.GetOutputSize(zip.Length)];
                int count = cipher.ProcessBytes(zip, 0, zip.Length, encrypted, 0); count += cipher.DoFinal(encrypted, count);
                var blob = new byte[36 + zip.Length];
                Buffer.BlockCopy(Encoding.ASCII.GetBytes("MWPKG001"), 0, blob, 0, 8);
                Buffer.BlockCopy(nonce, 0, blob, 8, 12);
                Buffer.BlockCopy(encrypted, zip.Length, blob, 20, 16);
                Buffer.BlockCopy(encrypted, 0, blob, 36, zip.Length);
                var id = Guid.NewGuid(); string path = "/api/packages/" + id.ToString("D") + "/download";
                Blobs.Add(path, blob);
                ((JArray)Payload["packages"]).Add(new JObject { ["id"] = id, ["code"] = code, ["version"] = version, ["sha256"] = Hash(blob), ["zipSha256"] = version, ["sizeBytes"] = blob.Length, ["key"] = Convert.ToBase64String(aes), ["downloadPath"] = path });
            }
            public Task<byte[]> GetManifest(string token, CancellationToken ct)
            {
                byte[] payload = Encoding.UTF8.GetBytes(Payload.ToString(Formatting.None));
                var signer = new Ed25519Signer(); signer.Init(true, key); signer.BlockUpdate(payload, 0, payload.Length);
                byte[] signature = signer.GenerateSignature(); if (BadSignature) signature[0] ^= 1;
                return Task.FromResult(Encoding.UTF8.GetBytes(new JObject { ["payload"] = Convert.ToBase64String(payload), ["signature"] = Convert.ToBase64String(signature) }.ToString(Formatting.None)));
            }
            public Task<byte[]> Download(string path, string token, CancellationToken ct)
            { Downloads.Add(path); return Task.FromResult(Blobs[path]); }
            public Task ReportDefinition(string hash, string token, CancellationToken ct) { Reports.Add(hash); return Task.CompletedTask; }
        }
    }
}
