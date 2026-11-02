using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MosWord2019.Projects;
using MosWord2019.Projects.OnlineSessions;
using MosWord2019.Projects.OnlineResults;

namespace MosWord2019
{
    public interface IOnlineAuthenticationService : IAuthenticationService
    {
        string LastErrorCode { get; }
        OnlineSessionCredentials Credentials { get; }
        Task<bool> AuthenticateAsync(string username, string password);
        Task<OnlineSessionContext> SelectModeAsync(AppMode mode, string language);
        Task CancelAsync();
    }
    public sealed class OnlineAuthenticationService : IOnlineAuthenticationService, IDisposable
    {
        private readonly ServerConnectionSettings settings;
        private readonly HttpClient http;
        private readonly OnlineSessionClient client;
        private readonly ResumeCredentialStore store;
        private readonly string initializationError;
        private Guid requestId;
        private string requestUsername;
        private bool successful;
        public OnlineSessionCredentials Credentials { get; private set; }
        public string LastErrorCode { get; private set; }
        public OnlineAuthenticationService()
        {
            try
            {
                settings = new ServerConnectionSettings(); http = settings.CreateClient();
                client = new OnlineSessionClient(settings.Origin, http); store = new ResumeCredentialStore();
                Credentials = Program.ResumedPendingMode; Program.ResumedPendingMode = null;
            }
            catch (Exception) { initializationError = "configuration_error"; }
        }
        public bool Authenticate(string username, string password) => AuthenticateAsync(username, password).GetAwaiter().GetResult();
        public async Task<bool> AuthenticateAsync(string username, string password)
        {
            if (initializationError != null) { LastErrorCode = initializationError; return false; }
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) { LastErrorCode = "missing_credentials"; return false; }
            if (requestId == Guid.Empty || requestUsername != username || successful)
            { requestId = Guid.NewGuid(); requestUsername = username; successful = false; }
            try
            {
                Credentials = await client.LoginAsync(username, password, MachineIdentity.GetMachineId(), requestId, CancellationToken.None);
                store.Save(Credentials); LastErrorCode = null; successful = true;
                _ = RetryPendingResultsAsync();
                return true;
            }
            catch (OnlineSessionException ex) { LastErrorCode = ex.ErrorCode; return false; }
        }
        private async Task RetryPendingResultsAsync()
        {
            try
            {
                using (var transport = new HttpResultUploadTransport(http))
                    await new ResultUploadQueue(transport).PumpAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { MosWord2019.Core.Diagnostics.AppLogger.Write("Post-login result retry deferred (" + ex.GetType().Name + ")."); }
        }
        public async Task<OnlineSessionContext> SelectModeAsync(AppMode mode, string language)
        {
            Credentials = await client.SelectModeAsync(Credentials, mode.ToString(), CancellationToken.None);
            Credentials.Language = language; store.Save(Credentials);
            var context = new OnlineSessionContext(Credentials, client, store, settings.Origin, http);
            try
            {
                context.Monitor.Start();
                await context.LoadPackagesAsync(Program.Journal ?? new SessionRecoveryJournal());
                _ = context.PumpResultsAsync();
                return context;
            }
            catch { context.Dispose(); throw; }
        }
        public async Task CancelAsync()
        {
            if (Credentials == null) return;
            try { await client.EndAsync(Credentials, CancellationToken.None); }
            finally { store.Delete(); Credentials = null; }
        }
        public void Dispose() { http?.Dispose(); }
    }
}
