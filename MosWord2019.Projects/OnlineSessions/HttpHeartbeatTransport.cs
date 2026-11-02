using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Projects.OnlineSessions
{
    /// <summary>Injected clients must retain normal TLS validation and disable automatic redirects.</summary>
    public sealed class HttpHeartbeatTransport : IHeartbeatTransport, IDisposable
    {
        private readonly Uri origin;
        private readonly HttpClient client;
        private readonly bool ownsClient;
        public HttpHeartbeatTransport(Uri origin, HttpClient client = null)
        {
            if (origin == null || !origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
                throw new ArgumentException("An HTTPS server origin is required.", nameof(origin));
            this.origin = origin; ownsClient = client == null;
            this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        }
        public async Task<HeartbeatReply> SendAsync(Guid sessionId, string token, long? clientElapsedMilliseconds, bool wasOffline, CancellationToken cancellationToken)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("A session is required.", nameof(sessionId));
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A session token is required.", nameof(token));
            if (clientElapsedMilliseconds < 0 || clientElapsedMilliseconds > 86400000) throw new ArgumentOutOfRangeException(nameof(clientElapsedMilliseconds));
            var target = new Uri(origin, "/api/sessions/" + sessionId.ToString("D") + "/heartbeat");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(HttpMethod.Post, target))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(new JObject { ["clientElapsedMilliseconds"] = clientElapsedMilliseconds.HasValue ? new JValue(clientElapsedMilliseconds.Value) : JValue.CreateNull(), ["wasOffline"] = wasOffline }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    if (response.RequestMessage == null || response.RequestMessage.RequestUri != target) throw new InvalidDataException("Redirected heartbeat response.");
                    int status = (int)response.StatusCode;
                    if (status == 401 || status == 410) throw new HeartbeatSessionRejectedException(status);
                    response.EnsureSuccessStatusCode();
                    if (response.Content == null || response.Content.Headers.ContentLength > 16384) throw new InvalidDataException("Invalid heartbeat response size.");
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[4096]; int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                        {
                            if (output.Length + count > 16384) throw new InvalidDataException("Heartbeat response is too large.");
                            output.Write(buffer, 0, count);
                        }
                        return ReadReply(Encoding.UTF8.GetString(output.ToArray()));
                    }
                }
            }
        }
        private static HeartbeatReply ReadReply(string body)
        {
            try
            {
                using (var text = new StringReader(body))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.DateTimeOffset })
                {
                    var json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidDataException("Trailing heartbeat response content.");
                    foreach (string key in new[] { "sessionId", "mode", "serverTime", "expiresAt", "remainingSeconds", "sessionToken" })
                        if (json.GetValue(key, StringComparison.OrdinalIgnoreCase) == null) throw new InvalidDataException("Incomplete heartbeat response.");
                    return json.ToObject<HeartbeatReply>();
                }
            }
            catch (JsonException ex) { throw new InvalidDataException("Invalid heartbeat JSON.", ex); }
        }
        public void Dispose() { if (ownsClient) client.Dispose(); }
    }
}
