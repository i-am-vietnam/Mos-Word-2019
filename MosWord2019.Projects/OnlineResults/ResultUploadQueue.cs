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
        public bool Parked { get; internal set; }
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
        private readonly Uri expectedOrigin;
        public ResultUploadQueue(IResultUploadTransport transport, string queueRoot = null, Func<DateTimeOffset> utcNow = null, Action<string> log = null, Uri expectedOrigin = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            this.log = log ?? (message => AppLogger.Write(message));
            if (expectedOrigin != null) HttpResultUploadTransport.ValidateOrigin(expectedOrigin);
            this.expectedOrigin = expectedOrigin;
            store = new ResultUploadStore(queueRoot);
        }
        public void Enqueue(string completedResultPath, Guid serverSessionId, Uri origin, byte[] signingKey, string uploadToken)
        {
            if (serverSessionId == Guid.Empty) throw new ArgumentException("A server session is required.", nameof(serverSessionId));
            HttpResultUploadTransport.ValidateOrigin(origin);
            if (expectedOrigin != null && origin != expectedOrigin) throw new ArgumentException("Upload origin differs from the configured server.", nameof(origin));
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
                    Receipt = record["receipt"] as JObject, Parked = store.IsParked(sessionId) };
            }
        }
        /// <summary>Runs due entries once. Metadata leases never span network I/O.</summary>
        public async Task PumpAsync(CancellationToken cancellationToken = default(CancellationToken), bool forceDue = false)
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
                    bool validating = false;
                    try
                    {
                        JObject record; byte[] payload;
                        using (store.AcquireLease())
                        {
                            validating = true; record = store.Read(id); payload = ValidateRecord(record, id); validating = false;
                            if (record["receipt"]?.Type == JTokenType.Object) { store.ArchiveReceipt(id); continue; }
                            if (expectedOrigin != null && new Uri((string)record["origin"]) != expectedOrigin)
                            { store.Park(id, "origin_mismatch", utcNow()); SafeLog(id, null, "parked: origin_mismatch"); continue; }
                            // The server owns the 24h permission window; local wall time must never prematurely park evidence.
                            if (!forceDue && DateTimeOffset.Parse((string)record["nextAttemptUtc"], System.Globalization.CultureInfo.InvariantCulture) > utcNow()) continue;
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
                            if (reply != null && reply.StatusCode >= 400 && reply.StatusCode < 500 && reply.StatusCode != 429)
                            {
                                using (store.AcquireLease()) store.Park(id, "http_" + reply.StatusCode, utcNow());
                                SafeLog(id, null, "parked: HTTP " + reply.StatusCode); continue;
                            }
                            if (reply == null || reply.StatusCode != 200) { SafeLog(id, null, "retry: response unavailable or temporary failure"); continue; }
                            var receipt = VerifyReceipt(reply.Body, id, (string)record["payloadHash"]);
                            using (store.AcquireLease())
                            {
                                record["receipt"] = receipt;
                                store.Write(id, record);
                                store.ArchiveReceipt(id);
                            }
                        }
                    }
                    catch (OperationCanceledException ex) { if (cancellationToken.IsCancellationRequested) throw; SafeLog(id, ex, "retry: request timed out"); }
                    catch (Exception ex)
                    {
                        if (validating && (ex is InvalidDataException || ex is JsonException || ex is CryptographicException || ex is ArgumentException || ex is FormatException || ex is InvalidCastException || ex is OverflowException))
                        {
                            try { using (store.AcquireLease()) store.Park(id, "invalid_record", utcNow()); SafeLog(id, ex, "parked: invalid protected record"); }
                            catch (Exception parkError) { SafeLog(id, parkError, "retry: parking unavailable; encrypted evidence retained"); }
                        }
                        else SafeLog(id, ex, "retry: operation failed; encrypted evidence retained");
                    }
                }
            }
            finally { pump.Release(); }
        }
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            bool firstPump = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                bool forceDue = firstPump; firstPump = false;
                try { await PumpAsync(cancellationToken, forceDue).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) { SafeLog(Guid.Empty, ex, "retry: queue scan unavailable"); }
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
        }
        private void SafeLog(Guid id, Exception error, string safeMessage)
        {
            // Never log arbitrary exception messages: transport/JSON errors may contain tokens or payload.
            try { log("Result upload item=" + id.ToString("N") + " exception=" + (error?.GetType().Name ?? "none") + " cause=" + DescribeFailure(error) + " message=" + safeMessage + "."); }
            catch { /* A failing diagnostic sink must not stop durable delivery. */ }
        }
        public static string DescribeFailure(Exception error)
        {
            if (error == null) return "none";
            // Return only a known literal, discarding parameters, inner exceptions and arbitrary suffixes.
            if (error is ArgumentException)
            {
                foreach (string known in new[] { "Invalid certificate pin.", "An HTTPS origin is required.",
                    "The upload store must be outside a repository.", "Upload origin differs from the configured server." })
                    if (error.Message.StartsWith(known, StringComparison.Ordinal)) return known;
            }
            var session = error as OnlineSessions.OnlineSessionException;
            if (session != null && session.ErrorCode == "certificate_mismatch") return "Server certificate or configured origin mismatch.";
            return "Unrecognized error details withheld.";
        }
        private static byte[] ValidateRecord(JObject record, Guid id)
        {
            Guid stored;
            if (record == null || (int?)record["version"] != 1 || !Guid.TryParse((string)record["sessionId"], out stored) || stored != id ||
                record["attempts"]?.Type != JTokenType.Integer || (long)record["attempts"] < 0 || (long)record["attempts"] > int.MaxValue ||
                !DateTimeOffset.TryParse((string)record["nextAttemptUtc"], out _) ||
                (record["receipt"]?.Type != JTokenType.Null && record["receipt"]?.Type != JTokenType.Object)) throw new InvalidDataException("Invalid protected upload record.");
            HttpResultUploadTransport.ValidateOrigin(new Uri((string)record["origin"]));
            string token = (string)record["token"];
            if (Convert.FromBase64String((string)record["key"]).Length != 32 || string.IsNullOrWhiteSpace(token) || token.Length > 16384 || token.Any(char.IsControl)) throw new InvalidDataException("Invalid protected upload credentials.");
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
