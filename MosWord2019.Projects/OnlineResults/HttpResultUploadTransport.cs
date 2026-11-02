using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MosWord2019.Projects.OnlineResults
{
    public sealed class ResultUploadResponse
    {
        public int StatusCode { get; }
        public byte[] Body { get; }
        public ResultUploadResponse(int statusCode, byte[] body) { StatusCode = statusCode; Body = body == null ? null : (byte[])body.Clone(); }
    }
    /// <summary>Implementations must send exact payload bytes over authenticated HTTPS, without redirects.</summary>
    public interface IResultUploadTransport
    {
        Task<ResultUploadResponse> SendAsync(Uri origin, string token, string signature, byte[] payload, CancellationToken cancellationToken);
    }
    public sealed class HttpResultUploadTransport : IResultUploadTransport, IDisposable
    {
        private readonly HttpClient client;
        private readonly bool ownsClient;
        public HttpResultUploadTransport() : this(null) { }
        /// <summary>Injected clients must retain normal TLS validation and disable automatic redirects.</summary>
        public HttpResultUploadTransport(HttpClient client)
        { ownsClient = client == null; this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }); }
        internal static void ValidateOrigin(Uri origin)
        {
            if (origin == null || !origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" ||
                origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
                throw new ArgumentException("An HTTPS origin is required.", nameof(origin));
        }
        public async Task<ResultUploadResponse> SendAsync(Uri origin, string token, string signature, byte[] payload, CancellationToken cancellationToken)
        {
            ValidateOrigin(origin);
            var target = new Uri(origin, "/api/results");
            using (var request = new HttpRequestMessage(HttpMethod.Post, target))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("X-Result-Signature", signature);
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (response.RequestMessage == null || response.RequestMessage.RequestUri != target) throw new InvalidDataException("Redirected result response.");
                    if (response.Content == null) return new ResultUploadResponse((int)response.StatusCode, null);
                    if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("Result receipt is too large.");
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[4096]; int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            if (output.Length + count > 65536) throw new InvalidDataException("Result receipt is too large.");
                            output.Write(buffer, 0, count);
                        }
                        return new ResultUploadResponse((int)response.StatusCode, output.ToArray());
                    }
                }
            }
        }
        public void Dispose() { if (ownsClient) client.Dispose(); }
    }
}
