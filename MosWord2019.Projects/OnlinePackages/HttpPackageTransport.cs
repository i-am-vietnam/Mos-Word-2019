using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace MosWord2019.Projects.OnlinePackages
{
    /// <summary>The injected client must disable automatic redirects and retain normal TLS validation.</summary>
    public sealed class HttpPackageTransport : IPackageTransport, IDisposable
    {
        private readonly Uri baseUri;
        private readonly HttpClient client;
        private readonly bool ownsClient;
        public event Action<string> SessionTokenRefreshed;
        public HttpPackageTransport(Uri baseUri, HttpClient client = null)
        {
            if (baseUri == null || baseUri.Scheme != Uri.UriSchemeHttps || baseUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
                throw new ArgumentException("An HTTPS server origin is required.", nameof(baseUri));
            this.baseUri = baseUri;
            ownsClient = client == null;
            this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        }
        public Task<byte[]> GetManifest(string sessionToken, CancellationToken cancellationToken)
        { return Read("/api/packages/manifest", sessionToken, 2 * 1024 * 1024, cancellationToken); }
        public async Task ReportDefinition(string definitionHash, string sessionToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken)) throw new ArgumentException("A session token is required.");
            using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/api/sessions/definition")))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
                request.Content = new StringContent(new JObject { ["definitionHash"] = definitionHash }.ToString(), Encoding.UTF8, "application/json");
                Uri target = request.RequestUri;
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage.RequestUri != target) throw new InvalidDataException("Redirected definition response.");
                    ConsumeSessionToken(response);
                }
            }
        }
        public Task<byte[]> Download(string downloadPath, string sessionToken, CancellationToken cancellationToken)
        {
            Guid id;
            const string prefix = "/api/packages/";
            const string suffix = "/download";
            if (downloadPath == null || downloadPath.Length <= prefix.Length + suffix.Length || !downloadPath.StartsWith(prefix, StringComparison.Ordinal) ||
                !downloadPath.EndsWith(suffix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(downloadPath.Substring(prefix.Length, downloadPath.Length - prefix.Length - suffix.Length), "D", out id))
                throw new InvalidDataException("Invalid package download path.");
            return Read(downloadPath, sessionToken, OnlinePackageCache.MaximumBlobBytes, cancellationToken);
        }
        private async Task<byte[]> Read(string path, string token, int limit, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A session token is required.");
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path)))
            {
                Uri target = request.RequestUri;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage.RequestUri != target) throw new InvalidDataException("Redirected package response.");
                    ConsumeSessionToken(response);
                    if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Package response is too large.");
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[81920];
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0)
                        {
                            if (output.Length + count > limit) throw new InvalidDataException("Package response is too large.");
                            output.Write(buffer, 0, count);
                        }
                        return output.ToArray();
                    }
                }
            }
        }
        private void ConsumeSessionToken(HttpResponseMessage response)
        {
            System.Collections.Generic.IEnumerable<string> values;
            if (!response.Headers.TryGetValues("Session-Token", out values)) return;
            var tokens = values.Take(2).ToArray();
            if (tokens.Length != 1 || string.IsNullOrWhiteSpace(tokens[0]) || tokens[0].Length > 4096 || tokens[0].Any(char.IsControl)) throw new InvalidDataException("Invalid refreshed session token.");
            SessionTokenRefreshed?.Invoke(tokens[0]);
        }
        public void Dispose() { if (ownsClient) client.Dispose(); }
    }
}
