using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Projects.OnlineSessions;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.X509;

namespace MosWord2019.Tests
{
    [TestClass]
    public class OnlineConnectionTests
    {
        private static readonly string Machine=new string('a',64);
        private static OnlineSessionCredentials Credentials() { return new OnlineSessionCredentials { SessionId=Guid.NewGuid(),SessionToken="access-secret",ResumeToken="resume-secret",MachineId=Machine,Mode="Testing",UploadToken="upload-secret",ResultSigningKey=Convert.ToBase64String(new byte[32]),RemainingUses=2,ServerTime=DateTimeOffset.UtcNow,Language="vi" }; }
        private static JObject Reply(OnlineSessionCredentials c)
        { return new JObject { ["sessionId"]=c.SessionId.ToString("D"),["sessionToken"]=c.SessionToken,["resumeToken"]=c.ResumeToken,["remainingUses"]=c.RemainingUses,["serverTime"]=c.ServerTime.ToString("O"),["mode"]=c.Mode,["modeSelectedAt"]=c.Mode==null ? JValue.CreateNull() : new JValue((c.ModeSelectedAt??c.ServerTime).ToString("O")),["examSeed"]=123,["definitionHash"]=new string('b',64),["uploadToken"]=c.UploadToken,["resultSigningKey"]=c.ResultSigningKey }; }
        [TestMethod]
        public async Task ModeSelectionCapturesServerTimeAndResumePreservesIt()
        {
            var state=Credentials(); state.Mode=null;
            var selectedAt=new DateTimeOffset(2026,11,2,10,0,0,TimeSpan.Zero);
            int call=0;
            using(var http=new HttpClient(new CallbackHandler(request=> {
                var responseState=Credentials(); responseState.SessionId=state.SessionId;
                responseState.ModeSelectedAt=selectedAt;
                responseState.ServerTime=selectedAt.AddMinutes(5+call++);
                return Task.FromResult(Response(request,Reply(responseState).ToString()));
            })))
            {
                var client=new OnlineSessionClient(new Uri("https://localhost/"),http);
                var selected=await client.SelectModeAsync(state,"Testing",CancellationToken.None);
                Assert.AreEqual(selectedAt,selected.ModeSelectedAt);
                var resumed=await client.ResumeAsync(selected,CancellationToken.None);
                Assert.AreEqual(selectedAt,resumed.ModeSelectedAt);
                Assert.AreEqual(selectedAt.AddMinutes(6),resumed.ServerTime);
                var repeated=await client.SelectModeAsync(resumed,"Testing",CancellationToken.None);
                Assert.AreEqual(selectedAt,repeated.ModeSelectedAt);
            }
        }
        [TestMethod]
        public async Task ModeRetryAfterLostAcknowledgementUsesPersistedServerSelectionTime()
        {
            var state=Credentials(); state.Mode=null;
            var selectedAt=new DateTimeOffset(2026,11,2,10,0,0,TimeSpan.Zero);
            int call=0;
            using(var http=new HttpClient(new CallbackHandler(request=> {
                if(++call==1) throw new HttpRequestException("Acknowledgement lost.");
                var serverState=Credentials(); serverState.SessionId=state.SessionId;
                serverState.ModeSelectedAt=selectedAt; serverState.ServerTime=selectedAt.AddMinutes(3);
                return Task.FromResult(Response(request,Reply(serverState).ToString()));
            })))
            {
                var client=new OnlineSessionClient(new Uri("https://localhost/"),http);
                await Assert.ThrowsExceptionAsync<OnlineSessionException>(()=>client.SelectModeAsync(state,"Testing",CancellationToken.None));
                Assert.IsNull(state.Mode); Assert.IsNull(state.ModeSelectedAt);
                var recovered=await client.SelectModeAsync(state,"Testing",CancellationToken.None);
                Assert.AreEqual(selectedAt,recovered.ModeSelectedAt);
                Assert.AreEqual(selectedAt.AddMinutes(3),recovered.ServerTime);
            }
        }
        [TestMethod]
        public async Task ModeSelectionRejectsChangedOrFutureServerSelectionTime()
        {
            var state=Credentials(); state.ModeSelectedAt=state.ServerTime.AddMinutes(-2);
            var response=Reply(state); response["modeSelectedAt"]=state.ServerTime.AddMinutes(-1).ToString("O");
            using(var http=new HttpClient(new ReplyHandler(response.ToString())))
                Assert.AreEqual("invalid_response",(await Assert.ThrowsExceptionAsync<OnlineSessionException>(()=>new OnlineSessionClient(new Uri("https://localhost/"),http).ResumeAsync(state,CancellationToken.None))).ErrorCode);
            response["modeSelectedAt"]=state.ServerTime.AddMinutes(1).ToString("O");
            using(var http=new HttpClient(new ReplyHandler(response.ToString())))
                Assert.AreEqual("invalid_response",(await Assert.ThrowsExceptionAsync<OnlineSessionException>(()=>new OnlineSessionClient(new Uri("https://localhost/"),http).ResumeAsync(state,CancellationToken.None))).ErrorCode);
        }
        [DataTestMethod]
        [DataRow(401,"invalid_credentials")]
        [DataRow(429,"retry_later")]
        public async Task HttpErrorPropagatesAuthenticatedResponseDate(int status,string code)
        {
            var serverTime=new DateTimeOffset(2026,11,2,10,0,0,TimeSpan.Zero);
            using(var http=new HttpClient(new CallbackHandler(request=> {
                var response=Response(request,"{\"error\":\"invalid_credentials\"}",(HttpStatusCode)status);
                response.Headers.Date=serverTime; return Task.FromResult(response);
            })))
            {
                var error=await Assert.ThrowsExceptionAsync<OnlineSessionException>(()=>
                    new OnlineSessionClient(new Uri("https://localhost/"),http).ResumeAsync(Credentials(),CancellationToken.None));
                Assert.AreEqual(code,error.ErrorCode); Assert.AreEqual(serverTime,error.ServerTime);
            }
        }
        [TestMethod]
        public async Task LoginRejectsWrongFieldTypeAndIdentity()
        {
            var json=Reply(Credentials()); json["sessionToken"]=true;
            var error=await LoginError(json.ToString()); Assert.AreEqual("invalid_response",error.ErrorCode);
        }
        [TestMethod]
        public async Task LoginMapsInvalidCredentialsWithoutServerText()
        {
            var error=await LoginError("{\"error\":\"invalid_credentials\",\"message\":\"secret\"}",HttpStatusCode.Unauthorized);
            Assert.AreEqual("invalid_credentials",error.ErrorCode); Assert.IsFalse(error.Message.Contains("secret"));
        }
        [DataTestMethod]
        [DataRow("no_uses_left")][DataRow("account_expired")][DataRow("account_locked")]
        public async Task AccountRefusalCodesRemainDistinct(string code)
        { Assert.AreEqual(code,(await LoginError("{\"error\":\""+code+"\"}",HttpStatusCode.Forbidden)).ErrorCode); }
        [TestMethod]
        public async Task JsonRejectsOversizeDuplicateUnknownAndTrailingContent()
        {
            var json=Reply(Credentials());
            foreach(string body in new[] { new string('x',32769),json.ToString()+"{}",json.ToString().Replace("\"remainingUses\": 2","\"remainingUses\": 2,\"remainingUses\": 2"),json.ToString().Replace("\"remainingUses\": 2","\"remainingUses\": -1") }) Assert.AreEqual("invalid_response",(await LoginError(body)).ErrorCode);
            json["unexpected"]=true; Assert.AreEqual("invalid_response",(await LoginError(json.ToString())).ErrorCode);
        }
        [TestMethod]
        public async Task RetryPreservesLoginIdempotencyAndMachineWithoutHiddenRetry()
        {
            var state=Credentials(); var key=Guid.NewGuid(); int count=0; string first=null;
            using(var http=new HttpClient(new CallbackHandler(async request=> {
                string body=await request.Content.ReadAsStringAsync(); if(++count==1) { first=body; throw new HttpRequestException("network"); }
                Assert.AreEqual(first,body); Assert.AreEqual(key.ToString("D"),(string)JObject.Parse(body)["idempotencyKey"]); return Response(request,Reply(state).ToString()); })))
            {
                var client=new OnlineSessionClient(new Uri("https://localhost/"),http);
                Assert.AreEqual("server_unreachable",(await Assert.ThrowsExceptionAsync<OnlineSessionException>(()=>client.LoginAsync("user","pass",Machine,key,CancellationToken.None))).ErrorCode);
                var reply=await client.LoginAsync("user","pass",Machine,key,CancellationToken.None); Assert.AreEqual(state.SessionId,reply.SessionId); Assert.AreEqual(2,count);
            }
        }
        [TestMethod]
        public async Task ResumeRejectsDifferentSessionAndPreservesUploadSecretsOnFailure()
        {
            var state=Credentials();
            using(var http=new HttpClient(new ReplyHandler(Reply(Credentials()).ToString())))
            {
                var client=new OnlineSessionClient(new Uri("https://localhost/"),http);
                Assert.AreEqual("invalid_response",(await Assert.ThrowsExceptionAsync<OnlineSessionException>(()=>client.ResumeAsync(state,CancellationToken.None))).ErrorCode);
                Assert.AreEqual("upload-secret",state.UploadToken); Assert.AreEqual("vi",state.Language);
            }
        }
        [TestMethod]
        public async Task EndValidatesReceiptAndRetainsQueueCredentials()
        {
            var state=Credentials(); var body=new JObject { ["sessionId"]=state.SessionId.ToString("D"),["serverTime"]=state.ServerTime.ToString("O"),["endedAt"]=state.ServerTime.ToString("O"),["sessionToken"]="refreshed" };
            using(var http=new HttpClient(new ReplyHandler(body.ToString())))
            { var reply=await new OnlineSessionClient(new Uri("https://localhost/"),http).EndAsync(state,CancellationToken.None); Assert.AreEqual("refreshed",reply.SessionToken); Assert.AreEqual(state.UploadToken,reply.UploadToken); Assert.AreEqual(state.ResultSigningKey,reply.ResultSigningKey); Assert.AreEqual("vi",reply.Language); }
        }
        [TestMethod]
        public void DpapiRestartWrongMachineTamperAndDeletion()
        {
            string root=Path.Combine(Path.GetTempPath(),"MosWord2019-C5-"+Guid.NewGuid().ToString("N"));
            try
            {
                var state=Credentials(); var store=new ResumeCredentialStore(root,Machine); Assert.IsNull(store.Load()); store.Save(state);
                string path=Path.Combine(root,"session.bin"); Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("resume-secret"));
                var restored=new ResumeCredentialStore(root,Machine).Load(); Assert.AreEqual(state.ResultSigningKey,restored.ResultSigningKey); Assert.AreEqual(state.UploadToken,restored.UploadToken); Assert.AreEqual("vi",restored.Language);
                Assert.ThrowsException<InvalidDataException>(()=>new ResumeCredentialStore(root,new string('c',64)).Load());
                byte[] encrypted=File.ReadAllBytes(path); encrypted[encrypted.Length/2]^=1; File.WriteAllBytes(path,encrypted); Assert.ThrowsException<CryptographicException>(()=>store.Load());
                store.Save(state); store.Delete(); Assert.IsNull(store.Load());
            }
            finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
        }
        [TestMethod]
        public void ResumeStoreRejectsRepositoryAndMachineIdentityIsHashedStable()
        {
            Assert.ThrowsException<ArgumentException>(()=>new ResumeCredentialStore(AppDomain.CurrentDomain.BaseDirectory,Machine));
            string value=MachineIdentity.GetMachineId(); Assert.AreEqual(64,value.Length); Assert.AreEqual(value,MachineIdentity.GetMachineId());
        }
        [TestMethod]
        public async Task RealTlsPinAcceptsSelfSignedRejectsDifferentCertificate()
        {
            using(var rsa=RSA.Create(2048))
            using(var cert=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1)))
            {
                byte[] spki=new X509CertificateParser().ReadCertificate(cert.RawData).CertificateStructure.SubjectPublicKeyInfo.GetDerEncoded(); byte[] pin;
                using(var sha=SHA256.Create()) pin=sha.ComputeHash(spki);
                Assert.IsTrue(PinnedHttpClientFactory.Matches(cert,pin,SslPolicyErrors.RemoteCertificateChainErrors));
                Assert.IsFalse(PinnedHttpClientFactory.Matches(cert,pin,SslPolicyErrors.RemoteCertificateNameMismatch));
                using(var serverCert=new X509Certificate2(cert.Export(X509ContentType.Pfx,"test"),"test",X509KeyStorageFlags.Exportable))
                {
                    await TlsRequest(serverCert,Convert.ToBase64String(pin),false);
                    await TlsRequest(serverCert,Convert.ToBase64String(new byte[32]),true);
                }
            }
        }
        private static async Task TlsRequest(X509Certificate2 certificate,string pin,bool reject)
        {
            var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); int port=((IPEndPoint)listener.LocalEndpoint).Port;
            Task server=Task.Run(async()=> {
                try
                {
                    using(var socket=await listener.AcceptTcpClientAsync()) using(var tls=new SslStream(socket.GetStream(),false))
                    { await tls.AuthenticateAsServerAsync(certificate,false,SslProtocols.Tls12,false); var buffer=new byte[8192]; await tls.ReadAsync(buffer,0,buffer.Length); byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"); await tls.WriteAsync(response,0,response.Length); }
                }
                catch(Exception ex) when(ex is IOException || ex is AuthenticationException) { if(!reject) throw; }
            });
            try
            {
                var origin=new Uri("https://localhost:"+port+"/");
                using(var client=PinnedHttpClientFactory.Create(origin,pin))
                {
                    Task<HttpResponseMessage> pending=client.GetAsync(origin);
                    Task first=await Task.WhenAny(server,pending);
                    if(first==server && server.IsFaulted) await server;
                    if(reject) Assert.AreEqual("certificate_mismatch",(await Assert.ThrowsExceptionAsync<OnlineSessionException>(async()=> { using(var response=await pending) { } })).ErrorCode);
                    else using(var response=await pending) Assert.AreEqual(HttpStatusCode.OK,response.StatusCode);
                }
                await server;
            }
            finally { listener.Stop(); }
        }
        private static async Task<OnlineSessionException> LoginError(string body,HttpStatusCode code=HttpStatusCode.OK)
        { using(var http=new HttpClient(new ReplyHandler(body,code))) return await Assert.ThrowsExceptionAsync<OnlineSessionException>(()=>new OnlineSessionClient(new Uri("https://localhost/"),http).LoginAsync("user","pass",Machine,Guid.NewGuid(),CancellationToken.None)); }
        private static HttpResponseMessage Response(HttpRequestMessage request,string body,HttpStatusCode code=HttpStatusCode.OK)
        { return new HttpResponseMessage(code) { RequestMessage=request,Content=new StringContent(body,Encoding.UTF8,"application/json") }; }
        private sealed class CallbackHandler : HttpMessageHandler
        { private readonly Func<HttpRequestMessage,Task<HttpResponseMessage>> callback; internal CallbackHandler(Func<HttpRequestMessage,Task<HttpResponseMessage>> callback) { this.callback=callback; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { return callback(request); } }
        private sealed class ReplyHandler : HttpMessageHandler
        {
            private readonly string body; private readonly HttpStatusCode code;
            internal ReplyHandler(string body,HttpStatusCode code=HttpStatusCode.OK) { this.body=body; this.code=code; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { return Task.FromResult(Response(request,body,code)); }
        }
    }
}
