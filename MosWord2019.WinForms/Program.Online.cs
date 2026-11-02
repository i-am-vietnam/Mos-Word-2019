using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
using MosWord2019.Projects.OnlinePackages;
using MosWord2019.Projects.OnlineSessions;
using MosWord2019.Word;

namespace MosWord2019
{
    internal static partial class Program
    {
        internal static OnlineSessionCredentials ResumedPendingMode { get; set; }
        private static bool InspectOnlineStartup()
        {
            var state = new TestingSessionStateStore();
            try
            {
                var credentialStore = new ResumeCredentialStore();
                var credentials = credentialStore.Load();
                if (credentials == null)
                {
                    if (!state.HasOnlineState) return false;
                    throw new InvalidDataException("Online recovery credentials are unavailable.");
                }
                if (state.HasOnlineState && state.PendingOnlineSessionId != credentials.SessionId)
                    throw new InvalidDataException("Online credentials and Testing state differ.");
                var settings = new ServerConnectionSettings();
                using (var http = settings.CreateClient())
                {
                    var client = new OnlineSessionClient(settings.Origin, http);
                    bool refused = false;
                    try
                    {
                        // Possession and same-machine permission are checked before any local resume or cleanup.
                        credentials = client.ResumeAsync(credentials, CancellationToken.None).GetAwaiter().GetResult();
                        credentialStore.Save(credentials);
                    }
                    catch (OnlineSessionException ex) when (ex.ErrorCode == "invalid_credentials" ||
                        ex.ErrorCode == "account_locked" || ex.ErrorCode == "account_expired")
                    {
                        if (!ex.ServerTime.HasValue) throw new InvalidDataException("The refusal did not provide authenticated server time.");
                        if (ex.ServerTime.Value > credentials.ServerTime) credentials.ServerTime = ex.ServerTime.Value;
                        refused = true;
                    }
                    if (credentials.Mode == null)
                    {
                        if (refused) { credentialStore.Delete(); return true; }
                        ResumedPendingMode = credentials;
                        return false;
                    }
                    var context = new OnlineSessionContext(credentials, client, credentialStore, settings.Origin, http);
                    state = new TestingSessionStateStore(utcNow: () => context.Clock.UtcNow);
                    bool preserve = false;
                    IDisposable retainedPackages = null;
                    try
                    {
                        string committed = Path.Combine(Path.GetDirectoryName(state.StatePath), "Results", credentials.SessionId.ToString("N") + ".json");
                        if (!state.HasOnlineState && credentials.Mode == "Testing" && File.Exists(committed))
                        {
                            context.RegisterResult(committed);
                            context.EndAsync().GetAwaiter().GetResult();
                            return true;
                        }
                        if (credentials.Mode == "Testing" && state.HasOnlineState)
                        {
                            if (state.PendingOnlineStatus == "Completed")
                            {
                                if (!ClosePreviousOwnedWord(state.ReadOwnedWord(), state.ReadRecoveryMachine()))
                                { preserve = true; DeferRecoveryToNextStart(); return true; }
                                context.RegisterResult(state.PersistCompletedResult());
                                state.ArchiveCompleted();
                                context.EndAsync().GetAwaiter().GetResult();
                                Journal.ReleaseSession(credentials.SessionId.ToString("N"));
                                Journal.DeferRecovery(false); Journal.Recover();
                                return true;
                            }
                            var record = state.LoadOnlineSaved();
                            string retainedRoot = Path.GetDirectoryName(state.RecoveryPackages[0].ProjectFolderPath);
                            retainedPackages = OnlinePackageCache.RetainRecoveryPackages(retainedRoot, Journal);
                            if (!ClosePreviousOwnedWord(record)) { preserve = true; DeferRecoveryToNextStart(); return true; }
                            state.RecordRecoveryDecision(record.Decide(context.Clock.UtcNow));
                            if (refused)
                            {
                                if (state.PendingOnlineStatus == "Prepared")
                                { state.DiscardPreparedOnline(credentials.SessionId); context.EndAsync().GetAwaiter().GetResult(); }
                                else TestingFlow.FinalizeOnlineSaved(context);
                                Journal.ReleaseSession(credentials.SessionId.ToString("N"));
                                Journal.DeferRecovery(false); Journal.Recover();
                                OnlinePackageCache.RecoverIncompletePackages(Journal);
                                MessageBox.Show("Server không cho tiếp tục phiên. Bài đã được chốt từ dữ liệu lưu và giữ để upload.\nThe server refused resume. Saved work was finalized and retained for upload.", "MOS Word 2019");
                                return true;
                            }
                        }
                        else if (refused)
                        {
                            context.EndAsync().GetAwaiter().GetResult();
                            MessageBox.Show("Phiên trước đã hết quyền sử dụng. Vui lòng đăng nhập lại.\nThe previous session is no longer authorized. Please log in again.", "MOS Word 2019");
                            return true;
                        }
                        context.Monitor.Start();
                        context.LoadPackagesAsync(Journal).GetAwaiter().GetResult();
                        Journal.DeferRecovery(false); Journal.Recover();
                        if (credentials.Mode == "Testing") TestingFlow.Open(null, context.Language, context);
                        else using (var form = new MainForm(new AppSession(AppMode.Training, context.Language, context))) form.ShowDialog();
                        preserve = RecoveryDeferred || (state.HasOnlineState && state.RequiresRecovery);
                        if (preserve) DeferRecoveryToNextStart();
                        else context.EndAsync().GetAwaiter().GetResult();
                        return true;
                    }
                    catch { preserve = true; throw; }
                    finally
                    {
                        if (preserve) context.Monitor.Dispose();
                        else context.Dispose();
                        retainedPackages?.Dispose();
                        if (!preserve) OnlinePackageCache.RecoverIncompletePackages(Journal);
                    }
                }
            }
            catch (Exception ex)
            {
                Journal.DeferRecovery(true); DeferRecoveryToNextStart();
                AppLogger.Write("Online startup recovery retained (" + ex.GetType().Name + ").");
                string reason = ex is OnlineSessionException online && online.ErrorCode == "certificate_mismatch"
                    ? "Chứng chỉ server không khớp / Server certificate mismatch."
                    : "Không thể xác thực hoặc khôi phục phiên / Session verification or recovery is unavailable.";
                MessageBox.Show(reason + "\nDữ liệu đã được giữ. Hãy kiểm tra mạng/cấu hình rồi mở lại ứng dụng.\nSaved work is retained. Check the connection/configuration and restart.", "MOS Word 2019", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
        }
        private static bool ClosePreviousOwnedWord(TestingRecoveryRecord record)
            => ClosePreviousOwnedWord(record.OwnedWord, record.Machine);
        private static bool ClosePreviousOwnedWord(OwnedWordProcessIdentity identity, string machine)
        {
            var process = new OwnedWordProcessRecovery();
            if (identity == null || !process.IsRunning(identity, machine)) return true;
            if (MessageBox.Show("Word của bài trước vẫn đang mở. Hãy lưu tài liệu cá nhân trong cửa sổ đó trước khi tiếp tục.\nThe previous exam's Word window is still open. Save personal documents in that window before continuing.",
                "MOS Word 2019", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return false;
            return process.Stop(identity, machine);
        }
    }
}
