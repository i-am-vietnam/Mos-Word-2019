using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
using MosWord2019.Projects.OnlinePackages;
using MosWord2019.Projects.OnlineResults;
using MosWord2019.Projects.OnlineSessions;

namespace MosWord2019
{
    public sealed class OnlineSessionContext : IDisposable
    {
        private readonly OnlineSessionClient client;
        private readonly ResumeCredentialStore store;
        private readonly Uri origin;
        private readonly HttpClient http;
        private readonly object gate = new object();
        private readonly Func<Task> endOverride;
        private readonly Action<string> registerOverride;
        private Task ending;
        private bool disposed;
        private long elapsedTicks = -1;
        private readonly List<OnlinePackageSession> trainingCaches = new List<OnlinePackageSession>();
        private readonly Dictionary<string, ProjectPackage> trainingPackages = new Dictionary<string, ProjectPackage>(StringComparer.Ordinal);
        public OnlineSessionCredentials Credentials { get; private set; }
        public OnlinePackageSession Packages { get; private set; }
        public string ProjectsRootPath { get; private set; }
        public IReadOnlyList<ProjectPackage> TrainingCatalog { get; private set; }
        public SessionConnectivityMonitor Monitor { get; }
        public ITestingClock Clock { get; }
        public string Language => Credentials.Language ?? "en";
        public string TerminalMessage { get; set; }
        public Func<TimeSpan?> ElapsedProvider { get; set; }

        internal OnlineSessionContext(OnlineSessionCredentials credentials, OnlineSessionClient client,
            ResumeCredentialStore store, Uri origin, HttpClient http)
        {
            Credentials = credentials; this.client = client; this.store = store; this.origin = origin; this.http = http;
            Monitor = new SessionConnectivityMonitor(credentials.SessionId, credentials.Mode, credentials.SessionToken,
                new HttpHeartbeatTransport(origin, http), new StopwatchTestingClock(), () =>
                {
                    var provider = ElapsedProvider;
                    long ticks = Interlocked.Read(ref elapsedTicks);
                    return provider != null ? provider() : ticks >= 0 ? TimeSpan.FromTicks(ticks) : (TimeSpan?)null;
                });
            Monitor.SessionTokenRefreshed += Refresh;
            Clock = new ServerSessionClock(credentials.ServerTime, () => Monitor.ProjectedServerTime,
                credentials.Mode == "Testing" ? credentials.ModeSelectedAt?.AddHours(2) : null);
        }
        internal OnlineSessionContext(OnlineSessionCredentials credentials, SessionConnectivityMonitor monitor,
            string projectsRoot, Func<Task> end, Action<string> register = null)
        {
            Credentials = credentials; Monitor = monitor; ProjectsRootPath = projectsRoot;
            endOverride = end; registerOverride = register;
            Clock = new ServerSessionClock(credentials.ServerTime, () => Monitor.ProjectedServerTime);
        }
        public void SetTestingElapsed(TimeSpan? elapsed) => Interlocked.Exchange(ref elapsedTicks, elapsed?.Ticks ?? -1);
        private void Refresh(string token)
        {
            lock (gate)
            {
                if (disposed || ending != null) return;
                Credentials.SessionToken = token;
                Credentials.ServerTime = Monitor.ProjectedServerTime ?? Credentials.ServerTime;
                try { store?.Save(Credentials); }
                catch (Exception ex) { AppLogger.Write("Online token persistence deferred (" + ex.GetType().Name + ")."); }
            }
        }
        internal async Task LoadPackagesAsync(SessionRecoveryJournal journal, CancellationToken ct = default(CancellationToken))
        {
            var transport = new HttpPackageTransport(origin, http);
            transport.SessionTokenRefreshed += Refresh;
            var cache = new OnlinePackageCache(transport, journal);
            if (Credentials.Mode == "Training")
            {
                var codes = await cache.TrainingCodesAsync(Credentials.SessionId, Credentials.SessionToken, ct).ConfigureAwait(false);
                TrainingCatalog = codes.Select(code => new ProjectPackage { Meta = new ProjectMeta { ProjectId = code } }).ToArray();
                ProjectsRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosWord2019", "Packages", Credentials.SessionId.ToString("N"));
                return;
            }
            try
            {
                Packages = await cache.LoadAsync(Credentials.SessionId, Credentials.Mode, null,
                    Credentials.SessionToken, ct).ConfigureAwait(false);
                if (Credentials.Mode == "Testing" && Credentials.DefinitionHash != Packages.Manifest.DefinitionHash)
                    throw new InvalidDataException("The signed exam does not match mode selection.");
                ProjectsRootPath = Packages.ProjectsRootPath;
            }
            catch { Packages?.Dispose(); Packages = null; throw; }
        }
        public async Task<ProjectPackage> GetTrainingPackageAsync(string code)
        {
            if (Credentials.Mode != "Training" || TrainingCatalog == null || !TrainingCatalog.Any(p => p.Meta.ProjectId == code))
                throw new InvalidDataException("The project is not in the verified Training manifest.");
            if (!Monitor.IsOnline || Monitor.IsAuthorizationExpired) throw new OnlineSessionException("server_unreachable");
            if (trainingPackages.TryGetValue(code, out var existing)) return existing;
            var transport = new HttpPackageTransport(origin, http);
            transport.SessionTokenRefreshed += Refresh;
            var cache = new OnlinePackageCache(transport, Program.Journal ?? new SessionRecoveryJournal());
            var loaded = await cache.LoadAsync(Credentials.SessionId, "Training", new[] { code }, Credentials.SessionToken).ConfigureAwait(false);
            try
            {
                if (!Monitor.IsOnline || Monitor.IsAuthorizationExpired) throw new OnlineSessionException("server_unreachable");
                var project = new ProjectLoader().Load(Path.Combine(loaded.ProjectsRootPath, code), Language);
                if (project == null) throw new InvalidDataException("The verified project could not be loaded.");
                trainingCaches.Add(loaded); trainingPackages.Add(code, project);
                return project;
            }
            catch { loaded.Dispose(); throw; }
        }
        public void RegisterResult(string resultPath)
        {
            if (registerOverride != null) { registerOverride(resultPath); return; }
            using (var transport = new HttpResultUploadTransport(http))
                new ResultUploadQueue(transport, expectedOrigin: origin).Enqueue(resultPath, Credentials.SessionId, origin,
                    Convert.FromBase64String(Credentials.ResultSigningKey), Credentials.UploadToken);
            // The startup worker also retries after a lost acknowledgement or process exit.
            _ = PumpResultsAsync();
        }
        internal async Task PumpResultsAsync()
        {
            try
            {
                using (var transport = new HttpResultUploadTransport(http))
                    await new ResultUploadQueue(transport, expectedOrigin: origin).PumpAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { AppLogger.Write("Online result retry deferred (" + ex.GetType().Name + ")."); }
        }
        public Task EndAsync()
        {
            lock (gate) return ending ?? (ending = EndCoreAsync());
        }
        private async Task EndCoreAsync()
        {
            Monitor?.Dispose();
            try
            {
                if (endOverride != null) await endOverride().ConfigureAwait(false);
                else if (client != null) await client.EndAsync(Credentials, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { AppLogger.Write("Server end acknowledgement unavailable (" + ex.GetType().Name + ")."); }
            finally
            {
                // A deliberate local end must never offer this use again at the next startup.
                store?.Delete();
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Monitor?.Dispose();
            Packages?.Dispose();
            foreach (var cache in trainingCaches) cache.Dispose();
        }
    }
}
