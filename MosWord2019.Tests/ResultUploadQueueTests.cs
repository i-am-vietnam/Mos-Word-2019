using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Projects.OnlineResults;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class ResultUploadQueueTests
    {
        [TestMethod]
        public async Task ExactBytesSignatureReceiptAndOriginalEvidenceSurviveRestart()
        {
            using (var f = new Fixture())
            {
                var q = f.Queue(); q.Enqueue(f.Result, f.Id, f.Origin, f.Key, f.Token);
                await q.PumpAsync();
                CollectionAssert.AreEqual(f.Payload, f.Transport.LastPayload);
                using (var hmac = new HMACSHA256(f.Key)) Assert.AreEqual(Convert.ToBase64String(hmac.ComputeHash(f.Payload)), f.Transport.LastSignature);
                Assert.AreEqual(f.Token, f.Transport.LastToken); Assert.AreEqual(f.Origin, f.Transport.LastOrigin);
                var status = f.Queue().GetStatus(f.Id); Assert.IsFalse(status.PendingUpload); Assert.AreEqual(1, status.Attempts);
                Assert.AreEqual(f.Id.ToString("D"), (string)status.Receipt["sessionId"]);
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
                Assert.IsTrue((bool)JObject.Parse(File.ReadAllText(f.Result))["pendingUpload"]);
                Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.upload").Length);
                Assert.AreEqual(1, Directory.GetFiles(Path.Combine(f.Root, "Receipts"), "*.receipt").Length);
                q.Enqueue(f.Result, f.Id, f.Origin, f.Key, f.Token); await f.Queue().PumpAsync(); Assert.AreEqual(1, f.Transport.Calls);
            }
        }
        [DataTestMethod]
        [DataRow(429)] [DataRow(503)] [DataRow(500)] [DataRow(302)] [DataRow(201)]
        public async Task Non200RetainsQueueAndBackoffAcrossRestart(int status)
        {
            using (var f = new Fixture())
            {
                f.Transport.Status = status; var q = f.Register(); await q.PumpAsync();
                Assert.IsTrue(q.GetStatus(f.Id).PendingUpload); Assert.AreEqual(f.Now.AddSeconds(5), q.GetStatus(f.Id).NextAttemptUtc);
                await f.Queue().PumpAsync(); Assert.AreEqual(1, f.Transport.Calls);
                f.Now = f.Now.AddSeconds(5); await f.Queue().PumpAsync(); Assert.AreEqual(2, f.Transport.Calls);
                Assert.AreEqual(f.Now.AddSeconds(10), q.GetStatus(f.Id).NextAttemptUtc);
                Assert.IsTrue(File.Exists(f.Result)); Assert.AreEqual(1, Directory.GetFiles(f.Root, "*.upload").Length);
            }
        }
        [DataTestMethod]
        [DataRow("hash")] [DataRow("uppercaseHash")] [DataRow("session")] [DataRow("score")] [DataRow("passed")]
        [DataRow("malformed")] [DataRow("duplicate")] [DataRow("trailing")] [DataRow("flags")] [DataRow("time")] [DataRow("empty")]
        public async Task InvalidReceiptNeverAcknowledges(string kind)
        {
            using (var f = new Fixture())
            {
                f.Transport.ReceiptMutation = kind; var q = f.Register(); await q.PumpAsync();
                Assert.IsTrue(q.GetStatus(f.Id).PendingUpload); Assert.IsNull(q.GetStatus(f.Id).Receipt);
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
                f.Transport.ReceiptMutation = null; f.Now = f.Now.AddSeconds(5); await f.Queue().PumpAsync();
                Assert.IsFalse(q.GetStatus(f.Id).PendingUpload); Assert.AreEqual(2, f.Transport.Calls);
            }
        }
        [TestMethod]
        public async Task LostAcknowledgementRetriesImmutableSnapshotEvenAfterSourceChanges()
        {
            using (var f = new Fixture())
            {
                f.Transport.LoseAcknowledgement = true; var q = f.Register(); await q.PumpAsync(); Assert.IsTrue(q.GetStatus(f.Id).PendingUpload);
                // Independent source damage cannot silently change the retry evidence.
                File.WriteAllText(f.Result, "changed by another program");
                f.Now = f.Now.AddSeconds(5); f.Transport.LoseAcknowledgement = false; await f.Queue().PumpAsync();
                CollectionAssert.AreEqual(f.Payload, f.Transport.LastPayload); Assert.IsFalse(q.GetStatus(f.Id).PendingUpload);
                Assert.AreEqual("changed by another program", File.ReadAllText(f.Result));
            }
        }
        [TestMethod]
        public async Task CancellationAndConcurrentPumpsDoNotSendDuplicates()
        {
            using (var f = new Fixture())
            using (var cancel = new CancellationTokenSource())
            {
                f.Transport.Block = true; var q = f.Register(); Task first = q.PumpAsync(cancel.Token);
                await f.Transport.Entered.Task;
                await q.PumpAsync(); await f.Queue().PumpAsync(); Assert.AreEqual(1, f.Transport.Calls);
                cancel.Cancel(); await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await first);
                Assert.IsTrue(f.Queue().GetStatus(f.Id).PendingUpload); Assert.AreEqual(1, q.GetStatus(f.Id).Attempts);
                f.Now = f.Now.AddSeconds(5); f.Transport.Block = false; await f.Queue().PumpAsync(); Assert.IsFalse(q.GetStatus(f.Id).PendingUpload);
            }
        }
        [TestMethod]
        public async Task TimeoutRetainsPendingAndDoesNotChangeEvidence()
        {
            using (var f = new Fixture())
            {
                f.Transport.Timeout = true; var q = f.Register(); await q.PumpAsync();
                Assert.IsTrue(q.GetStatus(f.Id).PendingUpload); Assert.AreEqual(1, q.GetStatus(f.Id).Attempts);
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
            }
        }
        [TestMethod]
        public void OnlineBindingRejectsSessionMismatchAndRebindingWithoutChangingResult()
        {
            using (var f = new Fixture())
            {
                var q = f.Queue(); Assert.ThrowsException<InvalidDataException>(() => q.Enqueue(f.Result, Guid.NewGuid(), f.Origin, f.Key, f.Token));
                Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.upload").Length);
                f.Register(); var key = (byte[])f.Key.Clone(); key[0]++;
                Assert.ThrowsException<InvalidDataException>(() => q.Enqueue(f.Result, f.Id, f.Origin, key, f.Token));
                Assert.ThrowsException<InvalidDataException>(() => q.Enqueue(f.Result, f.Id, new Uri("https://other.example/"), f.Key, f.Token));
                Assert.ThrowsException<InvalidDataException>(() => q.Enqueue(f.Result, f.Id, f.Origin, f.Key, "different-token"));
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
            }
        }
        [DataTestMethod]
        [DataRow("http://example.test/")] [DataRow("https://example.test/path")] [DataRow("https://user@example.test/")]
        [DataRow("https://example.test/?query=1")] [DataRow("https://example.test/#fragment")]
        public void OnlyHttpsOriginCanBeRegistered(string origin)
        {
            using (var f = new Fixture()) Assert.ThrowsException<ArgumentException>(() => f.Queue().Enqueue(f.Result, f.Id, new Uri(origin), f.Key, f.Token));
        }
        [TestMethod]
        public async Task OfflineHistoricalResultsAreNeverAutomaticallyRegistered()
        {
            using (var f = new Fixture()) { var q = f.Queue(); await q.PumpAsync(); Assert.AreEqual(0, f.Transport.Calls); Assert.IsNull(q.GetStatus(f.Id)); Assert.IsTrue(File.Exists(f.Result)); }
        }
        [TestMethod]
        public void QueueCredentialsAreCurrentUserDpapiProtectedAndDirectoryIsPrivate()
        {
            using (var f = new Fixture())
            {
                f.Register(); string path = Directory.GetFiles(f.Root, "*.upload").Single(); byte[] encrypted = File.ReadAllBytes(path);
                Assert.IsFalse(Encoding.UTF8.GetString(encrypted).Contains(f.Token)); Assert.IsFalse(Encoding.UTF8.GetString(encrypted).Contains(Convert.ToBase64String(f.Key)));
                byte[] plain = ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes("MosWord2019.ResultUpload.v1"), DataProtectionScope.CurrentUser);
                try { var record = JObject.Parse(Encoding.UTF8.GetString(plain)); Assert.AreEqual(f.Token, (string)record["token"]); Assert.AreEqual(Convert.ToBase64String(f.Key), (string)record["key"]); }
                finally { Array.Clear(plain, 0, plain.Length); }
                var security = Directory.GetAccessControl(f.Root); Assert.IsTrue(security.AreAccessRulesProtected);
                Assert.AreEqual(1, security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count);
            }
        }
        [TestMethod]
        public async Task CorruptProtectedRecordIsParkedIntactAndCannotBeAcknowledged()
        {
            using (var f = new Fixture())
            {
                var q = f.Register(); string path = Directory.GetFiles(f.Root, "*.upload").Single(); byte[] corrupt = File.ReadAllBytes(path); corrupt[corrupt.Length / 2] ^= 1; File.WriteAllBytes(path, corrupt);
                Guid goodId = Guid.NewGuid(); var second = JObject.Parse(Encoding.UTF8.GetString(f.Payload)); second["sessionId"] = goodId.ToString("D");
                string secondPath = Path.Combine(f.Parent, "good.json"); File.WriteAllText(secondPath, second.ToString()); q.Enqueue(secondPath, goodId, f.Origin, f.Key, f.Token);
                await q.PumpAsync(); Assert.AreEqual(1, f.Transport.Calls); Assert.IsFalse(q.GetStatus(goodId).PendingUpload);
                CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(Path.Combine(f.Root, "Parked", f.Id.ToString("N") + ".upload")));
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
            }
        }
        [TestMethod]
        public void OversizePayloadRejectedBeforeQueueRegistration()
        {
            using (var f = new Fixture()) { File.WriteAllBytes(f.Result, new byte[256 * 1024 + 1]); Assert.ThrowsException<InvalidDataException>(() => f.Register()); Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.upload").Length); }
        }
        [TestMethod]
        public async Task BackoffIsExponentialAndCappedAtOneHour()
        {
            using (var f = new Fixture())
            {
                f.Transport.Status = 503; var q = f.Register();
                for (int i = 0; i < 15; i++) { await q.PumpAsync(); var status = q.GetStatus(f.Id); Assert.AreEqual(f.Now.AddSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Min(i, 10)))), status.NextAttemptUtc); f.Now = status.NextAttemptUtc; }
                Assert.AreEqual(15, q.GetStatus(f.Id).Attempts); Assert.IsTrue(q.GetStatus(f.Id).PendingUpload);
            }
        }
        [TestMethod]
        public async Task HttpTransportSendsRawBytesAndRequiredHeaders()
        {
            using (var f = new Fixture())
            using (var client = new HttpClient(new RecordingHandler(async request =>
            {
                Assert.AreEqual("https://example.test/api/results", request.RequestUri.AbsoluteUri); Assert.AreEqual(HttpMethod.Post, request.Method);
                Assert.AreEqual("Bearer", request.Headers.Authorization.Scheme); Assert.AreEqual(f.Token, request.Headers.Authorization.Parameter);
                Assert.AreEqual("signature", request.Headers.GetValues("X-Result-Signature").Single()); CollectionAssert.AreEqual(f.Payload, await request.Content.ReadAsByteArrayAsync());
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{}") };
            })))
            using (var transport = new HttpResultUploadTransport(client))
            { Assert.AreEqual(200, (await transport.SendAsync(f.Origin, f.Token, "signature", f.Payload, CancellationToken.None)).StatusCode); }
        }
        [TestMethod]
        public async Task HttpTransportRejectsOversizeOrRedirectedResponses()
        {
            using (var f = new Fixture())
            {
                foreach (bool redirected in new[] { false, true })
                using (var client = new HttpClient(new RecordingHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = redirected ? new HttpRequestMessage(HttpMethod.Post, "https://other.test/") : request, Content = new ByteArrayContent(new byte[65537]) }))))
                using (var transport = new HttpResultUploadTransport(client))
                    await Assert.ThrowsExceptionAsync<InvalidDataException>(() => transport.SendAsync(f.Origin, f.Token, "signature", f.Payload, CancellationToken.None));
            }
        }
        [TestMethod]
        public async Task ServerRecomputedScoreIsAcceptedWhenHashAndSessionMatch()
        {
            using (var f = new Fixture())
            {
                f.Transport.ReceiptMutation = "correction"; var q = f.Register(); await q.PumpAsync();
                var status = f.Queue().GetStatus(f.Id); Assert.IsFalse(status.PendingUpload);
                Assert.AreEqual(699, (int)status.Receipt["score"]); Assert.IsFalse((bool)status.Receipt["passed"]);
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
            }
        }
        [TestMethod]
        public async Task GenuineB4ResultStillCommitsAndArchivesAfterOnlineAcknowledgement()
        {
            using (var f = new Fixture())
            {
                var state = new MosWord2019.Projects.TestingSessionStateStore(Path.Combine(f.Parent, "testing-session.json"));
                var prepared = state.Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), new MosWord2019.Projects.ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en"), "en");
                using (var session = new MosWord2019.Core.Models.TestingSession(prepared.SessionId, prepared.Definition, new TestingSessionTests.FakeClock(), new MosWord2019.Core.Interfaces.AlwaysOnlineMonitor()))
                {
                    session.Start(); session.BeginSubmit(new MosWord2019.Core.Models.ExamTaskGrade[0]);
                    for (int i = 1; i < 7; i++) session.RecordUnvisitedResults(i, new MosWord2019.Core.Models.ExamTaskGrade[0]);
                    session.Complete(_ => { }); state.Save(prepared, session, null);
                    string resultPath = state.PersistCompletedResult(); byte[] before = File.ReadAllBytes(resultPath); Guid id = Guid.Parse(prepared.SessionId);
                    f.Now = DateTimeOffset.Parse((string)JObject.Parse(Encoding.UTF8.GetString(before))["completedUtc"], System.Globalization.CultureInfo.InvariantCulture);
                    var q = f.Queue(); q.Enqueue(resultPath, id, f.Origin, f.Key, f.Token); await q.PumpAsync();
                    Assert.IsFalse(q.GetStatus(id).PendingUpload); CollectionAssert.AreEqual(before, File.ReadAllBytes(resultPath));
                    state.Save(prepared, session, DateTime.UtcNow); Assert.AreEqual(resultPath, state.PersistCompletedResult());
                    Assert.AreEqual(resultPath, state.ArchiveCompleted()); Assert.IsFalse(File.Exists(state.StatePath));
                    CollectionAssert.AreEqual(before, File.ReadAllBytes(resultPath));
                }
            }
        }
        [TestMethod]
        public void CredentialsCannotBeStoredUnderRepositoryRoot()
        {
            using (var f = new Fixture())
            {
                Directory.CreateDirectory(Path.Combine(f.Parent, ".git"));
                Assert.ThrowsException<ArgumentException>(() => f.Queue());
                Assert.IsFalse(Directory.Exists(f.Root));
            }
        }
        [TestMethod]
        public async Task RegisterAnotherResultWhileNetworkRequestIsBlocked()
        {
            using (var f = new Fixture())
            using (var cancel = new CancellationTokenSource())
            {
                f.Transport.Block = true; var q = f.Register(); var first = q.PumpAsync(cancel.Token); await f.Transport.Entered.Task;
                Guid secondId = Guid.NewGuid(); var second = JObject.Parse(Encoding.UTF8.GetString(f.Payload)); second["sessionId"] = secondId.ToString("D");
                string path = Path.Combine(f.Parent, "second-result.json"); File.WriteAllText(path, second.ToString());
                q.Enqueue(path, secondId, f.Origin, f.Key, f.Token);
                Assert.IsTrue(f.Queue().GetStatus(secondId).PendingUpload); Assert.AreEqual(0, q.GetStatus(secondId).Attempts);
                cancel.Cancel(); await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await first);
            }
        }
        [TestMethod]
        public async Task ReceiptCommitBeforeArchiveIsRecoveredWithoutNetworkReplay()
        {
            using (var f = new Fixture())
            {
                var q = f.Register(); await q.PumpAsync();
                File.Move(Directory.GetFiles(Path.Combine(f.Root, "Receipts"), "*.receipt").Single(), Path.Combine(f.Root, f.Id.ToString("N") + ".upload"));
                await f.Queue().PumpAsync(); Assert.AreEqual(1, f.Transport.Calls); Assert.IsFalse(q.GetStatus(f.Id).PendingUpload);
                Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.upload").Length); Assert.IsTrue(File.Exists(f.Result));
            }
        }
        [TestMethod]
        public void QueueBoundsPendingEntriesWithoutLimitingAcceptedHistory()
        {
            using (var f = new Fixture())
            {
                var q = f.Queue(); string receipts = Path.Combine(f.Root, "Receipts"); Directory.CreateDirectory(receipts);
                for (int i = 0; i < 1024; i++) File.WriteAllText(Path.Combine(receipts, Guid.NewGuid().ToString("N") + ".receipt"), "old receipt");
                q.Enqueue(f.Result, f.Id, f.Origin, f.Key, f.Token); Assert.IsTrue(q.GetStatus(f.Id).PendingUpload);
                for (int i = 0; i < 1023; i++) File.WriteAllText(Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".upload"), "pending");
                var body = JObject.Parse(Encoding.UTF8.GetString(f.Payload)); var another = Guid.NewGuid(); body["sessionId"] = another.ToString("D"); File.WriteAllText(f.Result, body.ToString());
                Assert.ThrowsException<IOException>(() => q.Enqueue(f.Result, another, f.Origin, f.Key, f.Token));
                Assert.IsTrue(File.Exists(f.Result)); Assert.AreEqual(1024, Directory.GetFiles(f.Root, "*.upload").Length);
            }
        }
        [TestMethod]
        public async Task UnexpectedTransportExceptionDoesNotAbortFollowingItem()
        {
            using (var f = new Fixture())
            {
                var q = f.Register(); f.Transport.FailOnce = true;
                Guid secondId = Guid.NewGuid();
                var second = JObject.Parse(Encoding.UTF8.GetString(f.Payload)); second["sessionId"] = secondId.ToString("D");
                string secondPath = Path.Combine(f.Parent, "second.json"); File.WriteAllText(secondPath, second.ToString());
                q.Enqueue(secondPath, secondId, f.Origin, f.Key, f.Token);
                await q.PumpAsync();
                Assert.AreEqual(2, f.Transport.Calls);
                Assert.AreEqual(1, new[] { q.GetStatus(f.Id), q.GetStatus(secondId) }.Count(status => !status.PendingUpload));
                Assert.AreEqual(1, Directory.GetFiles(f.Root, "*.upload").Length);
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
                f.Now = f.Now.AddSeconds(5); await q.PumpAsync();
                Assert.IsFalse(q.GetStatus(f.Id).PendingUpload); Assert.IsFalse(q.GetStatus(secondId).PendingUpload);
            }
        }
        [DataTestMethod]
        [DataRow(400)] [DataRow(401)] [DataRow(403)] [DataRow(404)] [DataRow(409)] [DataRow(410)] [DataRow(422)]
        public async Task PermanentHttpFailureParksEncryptedEvidence(int status)
        {
            using (var f = new Fixture())
            {
                var q = f.Register(); byte[] encrypted = File.ReadAllBytes(Path.Combine(f.Root, f.Id.ToString("N") + ".upload"));
                f.Transport.Status = status; await q.PumpAsync();
                Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.upload").Length);
                string parked = Directory.GetFiles(Path.Combine(f.Root, "Parked"), "*.upload").Single();
                // Only attempt metadata changed; payload/key/token remain DPAPI protected.
                Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(parked)).Contains(f.Token));
                string reason = File.ReadAllText(Path.ChangeExtension(parked, ".reason.json"));
                StringAssert.Contains(reason, "http_" + status); Assert.IsFalse(reason.Contains(f.Token));
                f.Now = f.Now.AddHours(1); await f.Queue().PumpAsync(); Assert.AreEqual(1, f.Transport.Calls);
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
            }
        }
        [TestMethod]
        public async Task LegacyWrongOriginIsParkedWithoutSendingAndGoodItemUploads()
        {
            using (var f = new Fixture())
            {
                var oldQueue = f.Queue(); oldQueue.Enqueue(f.Result, f.Id, new Uri("https://localhost:7118/"), f.Key, f.Token);
                Guid goodId = Guid.NewGuid(); var second = JObject.Parse(Encoding.UTF8.GetString(f.Payload)); second["sessionId"] = goodId.ToString("D");
                string secondPath = Path.Combine(f.Parent, "good.json"); File.WriteAllText(secondPath, second.ToString());
                oldQueue.Enqueue(secondPath, goodId, f.Origin, f.Key, f.Token);
                var bound = new ResultUploadQueue(f.Transport, f.Root, () => f.Now, _ => { }, f.Origin);
                await bound.PumpAsync();
                Assert.AreEqual(1, f.Transport.Calls); Assert.AreEqual(f.Origin, f.Transport.LastOrigin);
                Assert.IsTrue(bound.GetStatus(f.Id).Parked); Assert.IsTrue(bound.GetStatus(f.Id).PendingUpload);
                Assert.IsFalse(bound.GetStatus(goodId).PendingUpload);
                StringAssert.Contains(File.ReadAllText(Path.Combine(f.Root, "Parked", f.Id.ToString("N") + ".reason.json")), "origin_mismatch");
                oldQueue.Enqueue(f.Result, f.Id, new Uri("https://localhost:7118/"), f.Key, f.Token);
                Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.upload").Length);
            }
        }
        [DataTestMethod]
        [DataRow(24, 200, false)] [DataRow(25, 401, true)]
        public async Task ServerOwns24HourUploadWindow(int hours, int serverStatus, bool parked)
        {
            using (var f = new Fixture())
            {
                var body = JObject.Parse(Encoding.UTF8.GetString(f.Payload)); body["completedUtc"] = f.Now.ToString("O");
                f.Payload = Encoding.UTF8.GetBytes(body.ToString()); File.WriteAllBytes(f.Result, f.Payload);
                var q = f.Register(); f.Transport.Status = serverStatus;
                f.Now = f.Now.AddHours(hours); await q.PumpAsync();
                Assert.AreEqual(parked, q.GetStatus(f.Id).Parked); Assert.AreEqual(1, f.Transport.Calls);
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
            }
        }
        [TestMethod]
        public async Task InvalidProtectedRecordIsParkedAndFollowingGoodItemUploads()
        {
            using (var f = new Fixture())
            {
                var q = f.Register(); string path = Path.Combine(f.Root, f.Id.ToString("N") + ".upload");
                byte[] entropy = Encoding.UTF8.GetBytes("MosWord2019.ResultUpload.v1");
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
                var bad = JObject.Parse(Encoding.UTF8.GetString(plain)); Array.Clear(plain, 0, plain.Length);
                bad["origin"] = "http://localhost/";
                byte[] corrupted = ProtectedData.Protect(Encoding.UTF8.GetBytes(bad.ToString()), entropy, DataProtectionScope.CurrentUser); File.WriteAllBytes(path, corrupted);
                Guid goodId = Guid.NewGuid(); var second = JObject.Parse(Encoding.UTF8.GetString(f.Payload)); second["sessionId"] = goodId.ToString("D");
                string secondPath = Path.Combine(f.Parent, "good.json"); File.WriteAllText(secondPath, second.ToString()); q.Enqueue(secondPath, goodId, f.Origin, f.Key, f.Token);
                await q.PumpAsync();
                Assert.AreEqual(1, f.Transport.Calls); Assert.IsFalse(q.GetStatus(goodId).PendingUpload);
                CollectionAssert.AreEqual(corrupted, File.ReadAllBytes(Path.Combine(f.Root, "Parked", f.Id.ToString("N") + ".upload")));
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
            }
        }
        [TestMethod]
        public async Task WorkerSurvivesUnexpectedScanFailureAndRetriesWhileApplicationRuns()
        {
            using (var f = new Fixture())
            using (var cancel = new CancellationTokenSource())
            {
                var scanFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var q = new ResultUploadQueue(f.Transport, f.Root, () => f.Now, message =>
                {
                    Assert.IsFalse(message.Contains(f.Token));
                    if (message.Contains("queue scan unavailable")) scanFailed.TrySetResult(true);
                });
                for (int i = 0; i < 1025; i++) File.WriteAllText(Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".upload"), "isolated fixture");
                Task worker = q.RunAsync(cancel.Token);
                try
                {
                    Assert.AreEqual(scanFailed.Task, await Task.WhenAny(scanFailed.Task, worker, Task.Delay(2000)));
                    foreach (string path in Directory.GetFiles(f.Root, "*.upload")) File.Delete(path);
                    q.Enqueue(f.Result, f.Id, f.Origin, f.Key, f.Token);
                    Assert.AreEqual(f.Transport.Entered.Task, await Task.WhenAny(f.Transport.Entered.Task, worker, Task.Delay(7000)));
                    for (int i = 0; i < 100 && q.GetStatus(f.Id).PendingUpload; i++) await Task.Delay(20);
                    Assert.IsFalse(q.GetStatus(f.Id).PendingUpload); Assert.IsFalse(worker.IsCompleted);
                }
                finally { cancel.Cancel(); await worker; }
            }
        }
        [TestMethod]
        public void SafeFailureMessagePreservesKnownCauseAndNeverCopiesSecrets()
        {
            Assert.AreEqual("Invalid certificate pin.", ResultUploadQueue.DescribeFailure(new ArgumentException("Invalid certificate pin.", "sensitive-value")));
            Assert.AreEqual("An HTTPS origin is required.", ResultUploadQueue.DescribeFailure(new ArgumentException("An HTTPS origin is required.", "sensitive-value")));
            Assert.AreEqual("Upload origin differs from the configured server.", ResultUploadQueue.DescribeFailure(new ArgumentException("Upload origin differs from the configured server.", "sensitive-value")));
            Assert.AreEqual("The upload store must be outside a repository.", ResultUploadQueue.DescribeFailure(new ArgumentException("The upload store must be outside a repository.", "sensitive-value")));
            Assert.AreEqual("Unrecognized error details withheld.", ResultUploadQueue.DescribeFailure(new ArgumentException("sensitive-token and payload")));
            Assert.AreEqual("Server certificate or configured origin mismatch.", ResultUploadQueue.DescribeFailure(new MosWord2019.Projects.OnlineSessions.OnlineSessionException("certificate_mismatch")));
        }
        [TestMethod]
        public async Task PermanentHttpStatusSurvivesUntrustedOversizedErrorBody()
        {
            using (var f = new Fixture())
            using (var client = new HttpClient(new RecordingHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            { RequestMessage = request, Content = new ByteArrayContent(new byte[65537]) }))))
            using (var transport = new HttpResultUploadTransport(client))
            {
                var response = await transport.SendAsync(f.Origin, f.Token, "signature", f.Payload, CancellationToken.None);
                Assert.AreEqual(409, response.StatusCode); Assert.IsNull(response.Body);
            }
        }
        [TestMethod]
        public async Task LegacyResultWithOldLocalCompletionTimeStillAttemptsServerUpload()
        {
            using (var f = new Fixture())
            {
                var body = JObject.Parse(Encoding.UTF8.GetString(f.Payload)); body["completedUtc"] = f.Now.AddDays(-2).ToString("O");
                f.Payload = Encoding.UTF8.GetBytes(body.ToString()); File.WriteAllBytes(f.Result, f.Payload);
                var q = f.Register(); await q.PumpAsync();
                Assert.AreEqual(1, f.Transport.Calls); Assert.IsFalse(q.GetStatus(f.Id).PendingUpload);
                CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
            }
        }
        [TestMethod]
        public async Task WorkerStartupAttemptsPersistedBackoffImmediatelyThenKeepsNormalBackoff()
        {
            using (var f = new Fixture())
            using (var cancel = new CancellationTokenSource())
            {
                f.Transport.Status = 503; var q = f.Register(); await q.PumpAsync();
                Assert.AreEqual(f.Now.AddSeconds(5), q.GetStatus(f.Id).NextAttemptUtc);
                await f.Queue().PumpAsync(); Assert.AreEqual(1, f.Transport.Calls);
                Task worker = f.Queue().RunAsync(cancel.Token);
                try
                {
                    Assert.AreEqual(2, f.Transport.Calls);
                    Assert.AreEqual(f.Now.AddSeconds(10), q.GetStatus(f.Id).NextAttemptUtc);
                    await f.Queue().PumpAsync(); Assert.AreEqual(2, f.Transport.Calls);
                    f.Transport.Status = 200; await f.Queue().PumpAsync(forceDue: true);
                    Assert.AreEqual(3, f.Transport.Calls); Assert.IsFalse(q.GetStatus(f.Id).PendingUpload);
                    CollectionAssert.AreEqual(f.Payload, File.ReadAllBytes(f.Result));
                }
                finally { cancel.Cancel(); await worker; }
            }
        }
        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> send;
            internal RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) { this.send = send; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { return send(request); }
        }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Parent = Path.Combine(Path.GetTempPath(), "MosWord-result-queue-tests-" + Guid.NewGuid().ToString("N"));
            internal string Root => Path.Combine(Parent, "queue");
            internal string Result => Path.Combine(Parent, "result.json");
            internal readonly Guid Id = Guid.NewGuid(); internal readonly Uri Origin = new Uri("https://example.test/");
            internal readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            internal readonly string Token = "upload-token-only-for-test"; internal byte[] Payload;
            internal DateTimeOffset Now = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            internal readonly FakeTransport Transport = new FakeTransport();
            internal Fixture()
            {
                Directory.CreateDirectory(Parent);
                Payload = Encoding.UTF8.GetBytes(new JObject { ["version"] = 1, ["sessionId"] = Id.ToString("N"), ["pendingUpload"] = true,
                    ["total"] = 700, ["passed"] = true, ["definition"] = new JObject(), ["tasks"] = new JArray(), ["language"] = "Tiếng Việt" }.ToString());
                File.WriteAllBytes(Result, Payload);
            }
            internal ResultUploadQueue Queue() { return new ResultUploadQueue(Transport, Root, () => Now, _ => { }); }
            internal ResultUploadQueue Register() { var q = Queue(); q.Enqueue(Result, Id, Origin, Key, Token); return q; }
            public void Dispose() { if (Directory.Exists(Parent)) Directory.Delete(Parent, true); }
        }
        private sealed class FakeTransport : IResultUploadTransport
        {
            internal int Calls, Status = 200; internal string LastSignature, LastToken, ReceiptMutation; internal Uri LastOrigin; internal byte[] LastPayload;
            internal bool LoseAcknowledgement, Block, Timeout, FailOnce;
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<ResultUploadResponse> SendAsync(Uri origin, string token, string signature, byte[] payload, CancellationToken cancellationToken)
            {
                Calls++; LastOrigin = origin; LastToken = token; LastSignature = signature; LastPayload = (byte[])payload.Clone(); Entered.TrySetResult(true);
                if (FailOnce) { FailOnce = false; throw new InvalidOperationException("unsafe token must never be logged"); }
                if (Block) await Task.Delay(TimeoutInfinite, cancellationToken);
                if (Timeout) throw new OperationCanceledException();
                if (LoseAcknowledgement) throw new HttpRequestException("Acknowledgement lost after server commit.");
                var body = JObject.Parse(Encoding.UTF8.GetString(payload)); string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
                var receipt = new JObject { ["sessionId"] = Guid.Parse((string)body["sessionId"]).ToString("D"), ["payloadHash"] = hash, ["score"] = body["total"],
                    ["passed"] = body["passed"], ["flags"] = new JArray(), ["serverTime"] = "2026-11-02T00:00:00Z" };
                string text;
                switch (ReceiptMutation)
                {
                    case "hash": receipt["payloadHash"] = new string('0', 64); break;
                    case "uppercaseHash": receipt["payloadHash"] = hash.ToUpperInvariant(); break;
                    case "session": receipt["sessionId"] = Guid.NewGuid().ToString(); break;
                    case "score": receipt["score"] = -1; break;
                    case "correction": receipt["score"] = 699; receipt["passed"] = false; break;
                    case "passed": receipt["passed"] = false; break;
                    case "flags": receipt["flags"] = new JArray(1); break;
                    case "time": receipt["serverTime"] = "invalid"; break;
                }
                text = receipt.ToString();
                if (ReceiptMutation == "malformed") text = "{";
                if (ReceiptMutation == "empty") text = "";
                if (ReceiptMutation == "duplicate") text = text.Insert(1, "\"score\":700,");
                if (ReceiptMutation == "trailing") text += " {}";
                return new ResultUploadResponse(Status, Encoding.UTF8.GetBytes(text));
            }
            private const int TimeoutInfinite = -1;
        }
    }
}
