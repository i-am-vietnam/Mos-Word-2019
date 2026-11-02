using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.X509;
namespace MosWord2019.Projects.OnlineSessions
{
    public static class PinnedHttpClientFactory
    {
        public static HttpClient Create(Uri origin,string base64SpkiSha256)
        {
            OnlineSessionClient.ValidateOrigin(origin); byte[] pin;
            try { pin=Convert.FromBase64String(base64SpkiSha256); } catch(Exception ex) when(ex is ArgumentNullException || ex is FormatException) { throw new ArgumentException("Invalid certificate pin.",nameof(base64SpkiSha256)); }
            if(pin.Length!=32) throw new ArgumentException("Invalid certificate pin.",nameof(base64SpkiSha256));
            var marker=new PinHandler(origin); var handler=new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false,SslProtocols=System.Security.Authentication.SslProtocols.Tls12 };
            handler.ServerCertificateCustomValidationCallback=(request,certificate,chain,errors)=>
            {
                bool valid=Matches(certificate,pin,errors);
                if(!valid) request.Properties[PinHandler.Mismatch]=true;
                return valid;
            };
            marker.InnerHandler=handler; return new HttpClient(marker,true) { Timeout=TimeSpan.FromSeconds(30) };
        }
        internal static bool Matches(X509Certificate2 certificate,byte[] pin,SslPolicyErrors errors)
        {
            if(certificate==null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable|SslPolicyErrors.RemoteCertificateNameMismatch))!=0 || DateTime.UtcNow<certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow>certificate.NotAfter.ToUniversalTime()) return false;
            try
            {
                byte[] bytes=new X509CertificateParser().ReadCertificate(certificate.RawData).CertificateStructure.SubjectPublicKeyInfo.GetDerEncoded();
                using(var sha=SHA256.Create()) { byte[] actual=sha.ComputeHash(bytes); int difference=actual.Length^pin.Length; for(int i=0;i<actual.Length && i<pin.Length;i++) difference|=actual[i]^pin[i]; return difference==0; }
            }
            catch(CryptographicException) { return false; }
            catch(Org.BouncyCastle.Security.Certificates.CertificateException) { return false; }
        }
        private sealed class PinHandler : DelegatingHandler
        {
            internal const string Mismatch="MosWord2019.CertificateMismatch"; private readonly Uri origin;
            internal PinHandler(Uri origin) { this.origin=origin; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            {
                if(request.RequestUri.Scheme!=origin.Scheme || request.RequestUri.Host!=origin.Host || request.RequestUri.Port!=origin.Port) throw new OnlineSessionException("certificate_mismatch");
                try { return await base.SendAsync(request,ct).ConfigureAwait(false); }
                catch(HttpRequestException ex) { object mismatch; if(request.Properties.TryGetValue(Mismatch,out mismatch) && mismatch is bool && (bool)mismatch) throw new OnlineSessionException("certificate_mismatch",ex); throw; }
            }
        }
    }
}
