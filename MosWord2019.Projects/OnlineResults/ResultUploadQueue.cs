using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MosWord2019.Core.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Projects.OnlineResults
{
    public sealed class ResultUploadStatus
    {
        public bool PendingUpload { get; internal set; }
        public int Attempts { get; internal set; }
        public DateTimeOffset NextAttemptUtc { get; internal set; }
        public JObject Receipt { get; internal set; }
    }
    /// <summary>
    /// Explicit online registration only. Preserves the immutable local B4 result forever;
    /// pending status and verified receipts live in the separate protected ledger.
    /// </summary>
    public sealed class ResultUploadQueue
    {
        private readonly ResultUploadStore store;
        private readonly IResultUploadTransport transport;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly SemaphoreSlim pump = new SemaphoreSlim(1, 1);
        private readonly Action<string> log;
        public ResultUploadQueue(IResultUploadTransport transport, string queueRoot = null, Func<DateTimeOffset> utcNow = null, Action<string> log = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            this.log = log ?? (message => AppLogger.Write(message));
            store = new ResultUploadStore(queueRoot);
        }
        public void Enqueue(string completedResultPath, Guid serverSessionId, Uri origin, byte[] signingKey, string uploadToken)
        {
            if (serverSessionId == Guid.Empty) throw new ArgumentException("A server session is required.", nameof(serverSessionId));
            HttpResultUploadTransport.ValidateOrigin(origin);
            if (signingKey == null || signingKey.Length != 32) throw new ArgumentException("A 32-byte session signing key is required.", nameof(signingKey));
            if (string.IsNullOrWhiteSpace(uploadToken) || uploadToken.Length > 16384 || uploadToken.Any(char.IsControl)) throw new ArgumentException("A session upload token is required.", nameof(uploadToken));
            byte[] payload = ResultUploadStore.ReadBounded(completedResultPath, ResultUploadStore.MaximumPayloadBytes);
            var result = ResultUploadStore.Parse(payload); Guid payloadSession;
            if (!Guid.TryParse((string)result["sessionId"], out payloadSession) || payloadSession != serverSessionId)
                throw new InvalidDataException("The immutable result must already contain the server session ID.");
            if ((int?)result["version"] != 1 || result["pendingUpload"]?.Type != JTokenType.Boolean || (bool)result["pendingUpload"] != true ||
                result["total"]?.Type != JTokenType.Integer || (int)result["total"] < 0 || (int)result["total"] > 1000 || result["passed"]?.Type != JTokenType.Boolean ||
                !(result["tasks"] is JArray) || !(result["definition"] is JObject)) throw new InvalidDataException("A completed B4 result is required.");
            using (store.AcquireLease())
            {
                string hash = Hash(payload); var existing = store.Read(serverSessionId);
                if (existing != null)
                {
                    if ((string)existing["payloadHash"] != hash || (string)existing["origin"] != origin.AbsoluteUri ||
                        (string)existing["key"] != Convert.ToBase64String(signingKey) || (string)existing["token"] != uploadToken)
                        throw new InvalidDataException("An immutable upload binding already exists for this session.");
                    return;
                }
                if (store.Ids().Length >= 1024) throw new IOException("Upload queue is full; local result remains available.");
                store.Write(serverSessionId, new JObject
                {
                    ["version"] = 1, ["sessionId"] = serverSessionId.ToString("D"), ["origin"] = origin.AbsoluteUri,
                    ["key"] = Convert.ToBase64String(signingKey), ["token"] = uploadToken, ["payload"] = Convert.ToBase64String(payload),
                    ["payloadHash"] = hash, ["attempts"] = 0, ["nextAttemptUtc"] = utcNow().ToString("O"), ["receipt"] = null
                });
            }
        }
        public ResultUploadStatus GetStatus(Guid sessionId)
        {
            using (store.AcquireLease())
            {
                var record = store.Read(sessionId);
                if (record == null) return null;
                ValidateRecord(record, sessionId);
                return new ResultUploadStatus { PendingUpload = record["receipt"]?.Type != JTokenType.Object, Attempts = (int)record["attempts"],
                    NextAttemptUtc = DateTimeOffset.Parse((string)record["nextAttemptUtc"], System.Globalization.CultureInfo.InvariantCulture),
                    Receipt = record["receipt"] as JObject };
            }
        }
        /// <summary>Runs due entries once. Metadata leases never span network I/O.</summary>
        public async Task PumpAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!await pump.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
            try
            {
                FileStream lease;
                try { lease = store.AcquirePumpLease(); } catch (IOException) { return; }
                using (lease)
                foreach (Guid id in store.Ids())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        JObject record; byte[] payload;
                        using (store.AcquireLease())
                        {
                            record = store.Read(id); payload = ValidateRecord(record, id);
                            if (record["receipt"]?.Type == JTokenType.Object) { store.ArchiveReceipt(id); continue; }
                            if (DateTimeOffset.Parse((string)record["nextAttemptUtc"], System.Globalization.CultureInfo.InvariantCulture) > utcNow()) continue;
                            int attempts = (int)record["attempts"];
                            record["attempts"] = Math.Min(attempts + 1L, int.MaxValue);
                            // Commit before sending: a lost acknowledgement/crash retries the exact same bytes.
                            record["nextAttemptUtc"] = utcNow().AddSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts, 10)))).ToString("O");
                            store.Write(id, record);
                        }
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(15));
                            byte[] key = Convert.FromBase64String((string)record["key"]); string signature;
                            try { using (var hmac = new HMACSHA256(key)) signature = Convert.ToBase64String(hmac.ComputeHash(payload)); }
                            finally { Array.Clear(key, 0, key.Length); }
                            var reply = await transport.SendAsync(new Uri((string)record["origin"]), (string)record["token"], signature, payload, timeout.Token).ConfigureAwait(false);
                            timeout.Token.ThrowIfCancellationRequested();
                            if (reply == null || reply.StatusCode != 200) continue;
                            var receipt = VerifyReceipt(reply.Body, id, (string)record["payloadHash"]);
                            using (store.AcquireLease())
                            {
                                record["receipt"] = receipt;
                                store.Write(id, record);
                                store.ArchiveReceipt(id);
                            }
                        }
                    }
                    catch (OperationCanceledException) { if (cancellationToken.IsCancellationRequested) throw; }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException || ex is CryptographicException || ex is ArgumentException || ex is FormatException || ex is InvalidCastException || ex is OverflowException || ex is System.Net.Http.HttpRequestException || ex is UnauthorizedAccessException)
                    { log("Result upload deferred; local evidence and queue retained (" + ex.GetType().Name + ")."); }
                }
            }
            finally { pump.Release(); }
        }
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await PumpAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException)
                { log("Result queue retry deferred (" + ex.GetType().Name + ")."); }
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
        }
        private static byte[] ValidateRecord(JObject record, Guid id)
        {
            Guid stored;
            if (record == null || (int?)record["version"] != 1 || !Guid.TryParse((string)record["sessionId"], out stored) || stored != id ||
                record["attempts"]?.Type != JTokenType.Integer || (long)record["attempts"] < 0 || (long)record["attempts"] > int.MaxValue ||
                !DateTimeOffset.TryParse((string)record["nextAttemptUtc"], out _) ||
                (record["receipt"]?.Type != JTokenType.Null && record["receipt"]?.Type != JTokenType.Object)) throw new InvalidDataException("Invalid protected upload record.");
            HttpResultUploadTransport.ValidateOrigin(new Uri((string)record["origin"]));
            if (Convert.FromBase64String((string)record["key"]).Length != 32 || string.IsNullOrWhiteSpace((string)record["token"])) throw new InvalidDataException("Invalid protected upload credentials.");
            byte[] payload = Convert.FromBase64String((string)record["payload"]);
            if (payload.Length == 0 || payload.Length > ResultUploadStore.MaximumPayloadBytes || Hash(payload) != (string)record["payloadHash"]) throw new InvalidDataException("Upload payload changed.");
            if (record["receipt"] is JObject) VerifyReceipt(Encoding.UTF8.GetBytes(record["receipt"].ToString()), id, (string)record["payloadHash"]);
            return payload;
        }
        private static JObject VerifyReceipt(byte[] bytes, Guid id, string hash)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 65536) throw new InvalidDataException("Invalid result receipt size.");
            var receipt = ResultUploadStore.Parse(bytes); Guid receiptId; DateTimeOffset serverTime;
            if (!Guid.TryParse((string)receipt["sessionId"], out receiptId) || receiptId != id || receipt["payloadHash"]?.Type != JTokenType.String || (string)receipt["payloadHash"] != hash ||
                receipt["score"]?.Type != JTokenType.Integer || (int?)receipt["score"] < 0 || (int?)receipt["score"] > 1000 || receipt["passed"]?.Type != JTokenType.Boolean || (bool?)receipt["passed"] != ((int)receipt["score"] >= 700) ||
                !(receipt["flags"] is JArray) || ((JArray)receipt["flags"]).Any(f => f.Type != JTokenType.String) ||
                receipt["serverTime"]?.Type != JTokenType.String || !DateTimeOffset.TryParse((string)receipt["serverTime"], out serverTime) ||
                (receipt["sessionToken"] != null && receipt["sessionToken"].Type != JTokenType.String && receipt["sessionToken"].Type != JTokenType.Null))
                throw new InvalidDataException("Result receipt does not acknowledge this exact payload.");
            return receipt;
        }
        private static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
