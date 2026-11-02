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
    public sealed class OnlineSessionCredentials
    {
        public Guid SessionId { get; set; }
        public string SessionToken { get; set; }
        public string ResumeToken { get; set; }
        public string MachineId { get; set; }
        public string Mode { get; set; }
        public string UploadToken { get; set; }
        public string ResultSigningKey { get; set; }
        public int RemainingUses { get; set; }
        public DateTimeOffset ServerTime { get; set; }
        public DateTimeOffset? ModeSelectedAt { get; set; }
        public string Language { get; set; }
        public long? ExamSeed { get; set; }
        public string DefinitionHash { get; set; }
    }
    public sealed class OnlineSessionException : Exception
    {
        public string ErrorCode { get; private set; }
        public DateTimeOffset? ServerTime { get; internal set; }
        public OnlineSessionException(string code, Exception inner = null) : base(MessageFor(code), inner) { ErrorCode = code; }
        private static string MessageFor(string code)
        {
            switch(code)
            {
                case "invalid_credentials": return "Invalid credentials. Please try again later.";
                case "no_uses_left": return "No uses remain.";
                case "account_expired": return "The account has expired.";
                case "account_locked": return "The account is locked.";
                case "certificate_mismatch": return "The server certificate does not match the trusted pin.";
                case "server_unreachable": return "The server is unreachable.";
                default: return "The request could not be completed. Please try again later.";
            }
        }
    }
    /// <summary>Caller supplies a redirect-disabled, pinned client and retains ownership.</summary>
    public sealed class OnlineSessionClient
    {
        private readonly Uri origin; private readonly HttpClient client;
        public OnlineSessionClient(Uri origin, HttpClient client)
        { ValidateOrigin(origin); this.origin=origin; this.client=client ?? throw new ArgumentNullException(nameof(client)); }
        internal static void ValidateOrigin(Uri origin)
        {
            if (origin == null || !origin.IsAbsoluteUri || origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
                throw new ArgumentException("An HTTPS origin is required.", nameof(origin));
        }
        public Task<OnlineSessionCredentials> LoginAsync(string username,string password,string machineId,Guid idempotencyKey,CancellationToken ct)
        {
            if (string.IsNullOrEmpty(username) || username.Length>64 || string.IsNullOrEmpty(password) || password.Length>128 || idempotencyKey==Guid.Empty || !ValidMachine(machineId)) throw new ArgumentException("Invalid login request.");
            return SendAsync("/api/auth/login",new JObject { ["username"]=username,["password"]=password,["machineId"]=machineId,["idempotencyKey"]=idempotencyKey.ToString("D") },null,machineId,null,false,ct);
        }
        public Task<OnlineSessionCredentials> SelectModeAsync(OnlineSessionCredentials state,string mode,CancellationToken ct)
        { CheckState(state); if(mode!="Training" && mode!="Testing") throw new ArgumentException("Invalid session mode."); return SendAsync("/api/sessions/mode",new JObject { ["mode"]=mode },state,state.MachineId,mode,false,ct); }
        public Task<OnlineSessionCredentials> ResumeAsync(OnlineSessionCredentials state,CancellationToken ct)
        { CheckState(state); return SendAsync("/api/sessions/resume",new JObject { ["resumeToken"]=state.ResumeToken,["machineId"]=state.MachineId },state,state.MachineId,state.Mode,false,ct); }
        public Task<OnlineSessionCredentials> EndAsync(OnlineSessionCredentials state,CancellationToken ct)
        { CheckState(state); return SendAsync("/api/sessions/"+state.SessionId.ToString("D")+"/end",new JObject(),state,state.MachineId,state.Mode,true,ct); }
        internal static bool ValidMachine(string value)
        { if(value==null || value.Length!=64) return false; foreach(char c in value) if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F'))) return false; return true; }
        private static void CheckState(OnlineSessionCredentials state)
        { if(state==null || state.SessionId==Guid.Empty || !ValidMachine(state.MachineId) || string.IsNullOrWhiteSpace(state.SessionToken) || string.IsNullOrWhiteSpace(state.ResumeToken)) throw new ArgumentException("Invalid session credentials."); }
        private async Task<OnlineSessionCredentials> SendAsync(string path,JObject body,OnlineSessionCredentials state,string machine,string mode,bool end,CancellationToken ct)
        {
            try
            {
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct))
                using(var request=new HttpRequestMessage(HttpMethod.Post,new Uri(origin,path)))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    if(state!=null && path!="/api/sessions/resume") request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",state.SessionToken);
                    request.Content=new StringContent(body.ToString(Formatting.None),Encoding.UTF8,"application/json");
                    using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                    {
                        if(response.RequestMessage==null || response.RequestMessage.RequestUri!=request.RequestUri) throw new OnlineSessionException("invalid_response");
                        int status=(int)response.StatusCode;
                        if(status==429 || status>=500) throw new OnlineSessionException("retry_later") { ServerTime=response.Headers.Date };
                        JObject json=await ReadAsync(response,timeout.Token).ConfigureAwait(false);
                        if(status!=200)
                        {
                            string code=json["error"]?.Type==JTokenType.String ? (string)json["error"] : null;
                            if(status==401) code="invalid_credentials";
                            else if(status!=403 || (code!="no_uses_left" && code!="account_expired" && code!="account_locked")) code="retry_later";
                            throw new OnlineSessionException(code) { ServerTime=response.Headers.Date };
                        }
                        return Reply(json,state,machine,mode,end);
                    }
                }
            }
            catch(OperationCanceledException ex) when(!ct.IsCancellationRequested) { throw new OnlineSessionException("server_unreachable",ex); }
            catch(HttpRequestException ex) { throw new OnlineSessionException("server_unreachable",ex); }
            catch(IOException ex) { throw new OnlineSessionException("invalid_response",ex); }
            catch(JsonException ex) { throw new OnlineSessionException("invalid_response",ex); }
            catch(DecoderFallbackException ex) { throw new OnlineSessionException("invalid_response",ex); }
            catch(FormatException ex) { throw new OnlineSessionException("invalid_response",ex); }
        }
        private static async Task<JObject> ReadAsync(HttpResponseMessage response,CancellationToken ct)
        {
            if(response.Content==null || response.Content.Headers.ContentLength>32768) throw new OnlineSessionException("invalid_response");
            using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using(var output=new MemoryStream())
            {
                byte[] buffer=new byte[4096]; int n;
                while((n=await input.ReadAsync(buffer,0,buffer.Length,ct).ConfigureAwait(false))>0) { if(output.Length+n>32768) throw new OnlineSessionException("invalid_response"); output.Write(buffer,0,n); }
                using(var text=new StringReader(new UTF8Encoding(false,true).GetString(output.ToArray())))
                using(var reader=new JsonTextReader(text) { DateParseHandling=DateParseHandling.None,MaxDepth=16 })
                { var json=JObject.Load(reader,new JsonLoadSettings { DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error }); if(reader.Read()) throw new OnlineSessionException("invalid_response"); return json; }
            }
        }
        internal static string Field(JObject json,string name,bool nullable=false)
        { var t=json[name]; if(nullable && (t==null || t.Type==JTokenType.Null)) return null; if(t?.Type!=JTokenType.String || ((string)t).Length==0 || ((string)t).Length>16384) throw new OnlineSessionException("invalid_response"); return (string)t; }
        private static OnlineSessionCredentials Reply(JObject json,OnlineSessionCredentials state,string machine,string mode,bool end)
        {
            string[] allowed=end ? new[] { "sessionId","serverTime","endedAt","sessionToken" } : new[] { "sessionId","sessionToken","resumeToken","remainingUses","serverTime","mode","modeSelectedAt","examSeed","definitionHash","uploadToken","resultSigningKey" };
            foreach(var property in json.Properties()) if(Array.IndexOf(allowed,property.Name)<0) throw new OnlineSessionException("invalid_response");
            foreach(string name in allowed) if(json[name]==null) throw new OnlineSessionException("invalid_response");
            Guid id; DateTimeOffset time;
            if(!Guid.TryParseExact(Field(json,"sessionId"),"D",out id) || id==Guid.Empty || (state!=null && id!=state.SessionId) || !DateTimeOffset.TryParseExact(Field(json,"serverTime"),"O",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out time))
            {
                // System.Text.Json emits fractional seconds at variable precision.
                if(!Guid.TryParseExact(Field(json,"sessionId"),"D",out id) || id==Guid.Empty || (state!=null && id!=state.SessionId) || !DateTimeOffset.TryParse(Field(json,"serverTime"),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out time)) throw new OnlineSessionException("invalid_response");
            }
            if(!System.Text.RegularExpressions.Regex.IsMatch(Field(json,"serverTime"),@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z")) throw new OnlineSessionException("invalid_response");
            if(end) { DateTimeOffset endedAt; if(!DateTimeOffset.TryParse(Field(json,"endedAt"),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out endedAt) || endedAt>time) throw new OnlineSessionException("invalid_response"); }
            else
            {
                long seedValue;
                if(json["examSeed"].Type!=JTokenType.Null && (json["examSeed"].Type!=JTokenType.Integer || !long.TryParse(json["examSeed"].ToString(),out seedValue))) throw new OnlineSessionException("invalid_response");
                string hash=Field(json,"definitionHash",true); if(hash!=null && !ValidMachine(hash)) throw new OnlineSessionException("invalid_response");
            }
            string resultMode=end ? state.Mode : Field(json,"mode",true);
            if((resultMode!=null && resultMode!="Training" && resultMode!="Testing") || (mode!=null && resultMode!=mode)) throw new OnlineSessionException("invalid_response");
            int remaining=state?.RemainingUses??0;
            if(!end && (json["remainingUses"]?.Type!=JTokenType.Integer || !int.TryParse(json["remainingUses"].ToString(),out remaining) || remaining<0)) throw new OnlineSessionException("invalid_response");
            string upload=end ? state.UploadToken : Field(json,"uploadToken",true), key=end ? state.ResultSigningKey : Field(json,"resultSigningKey",true);
            foreach(string token in new[] { Field(json,"sessionToken"),end ? state.ResumeToken : Field(json,"resumeToken"),upload })
                if(token!=null) foreach(char c in token) if(char.IsWhiteSpace(c) || char.IsControl(c)) throw new OnlineSessionException("invalid_response");
            if(!end && ((key!=null && Convert.FromBase64String(key).Length!=32) || (resultMode=="Testing" && (string.IsNullOrWhiteSpace(upload) || key==null)))) throw new OnlineSessionException("invalid_response");
            long? seed=end ? state.ExamSeed : json["examSeed"].Type==JTokenType.Null ? (long?)null : (long)json["examSeed"];
            string definitionHash=end ? state.DefinitionHash : Field(json,"definitionHash",true);
            if(resultMode=="Testing" && !end && (!seed.HasValue || definitionHash==null)) throw new OnlineSessionException("invalid_response");
            DateTimeOffset? modeSelectedAt=state?.ModeSelectedAt;
            if(!end)
            {
                string selectedText=Field(json,"modeSelectedAt",true);
                if(resultMode==null)
                {
                    if(selectedText!=null || modeSelectedAt.HasValue) throw new OnlineSessionException("invalid_response");
                }
                else
                {
                    DateTimeOffset selected;
                    if(selectedText==null || !System.Text.RegularExpressions.Regex.IsMatch(selectedText,@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)\z") ||
                        !DateTimeOffset.TryParse(selectedText,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out selected) ||
                        selected.Offset!=TimeSpan.Zero || selected>time || (modeSelectedAt.HasValue && modeSelectedAt.Value!=selected)) throw new OnlineSessionException("invalid_response");
                    modeSelectedAt=selected;
                }
            }
            return new OnlineSessionCredentials { SessionId=id,SessionToken=Field(json,"sessionToken"),ResumeToken=end?state.ResumeToken:Field(json,"resumeToken"),MachineId=machine.ToLowerInvariant(),Mode=resultMode,UploadToken=upload,ResultSigningKey=key,RemainingUses=remaining,ServerTime=time,ModeSelectedAt=modeSelectedAt,Language=state?.Language,ExamSeed=seed,DefinitionHash=definitionHash };
        }
    }
}
