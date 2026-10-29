using Microsoft.Win32.SafeHandles;
using MosWord2019.Core.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MosWord2019.Projects
{
    /// <summary>Durable exact-file claims. Call only after proving session ownership.</summary>
    public sealed class SessionRecoveryJournal
    {
        private readonly string journalPath;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private readonly Dictionary<string, Claim> entries = new Dictionary<string, Claim>(StringComparer.OrdinalIgnoreCase);
        private bool invalid;

        public SessionRecoveryJournal(string path = null, Action<string> log = null)
        {
            journalPath = Normalize(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MosWord2019", "session-journal.json"));
            this.log = log ?? (message => AppLogger.Write(message));
            SessionId = Guid.NewGuid().ToString("N");
            Load();
        }

        public string SessionId { get; }
        public int Count { get { lock (gate) return entries.Count; } }

        public void Track(string path, string kind, string sessionId = null)
        {
            lock (gate) TrackFile(path, kind, sessionId);
        }

        private void TrackFile(string path, string kind, string sessionId)
        {
            path = Normalize(path);
            RejectReparse(path);
            using (var handle = Open(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (var stream = new FileStream(handle, FileAccess.Read))
                {
                    var claim = ReadFacts(handle, stream);
                    RejectReparse(path);
                    Track(path, claim.Identity, claim.Hash, kind, sessionId);
                }
            }
        }

        // Assets already hold an exclusive creation handle; do not reopen their file here.
        public void Track(string path, string identity, string sha256, string kind, string sessionId = null)
        {
            lock (gate) TrackFacts(path, identity, sha256, kind, sessionId);
        }

        private void TrackFacts(string path, string identity, string sha256, string kind, string sessionId)
        {
            EnsureValid();
            path = Normalize(path);
            var claim = new Claim { Path = path, Identity = identity, Hash = sha256, Kind = kind, SessionId = sessionId ?? SessionId };
            Validate(claim);
            RejectReparse(path);
            Claim old;
            if (entries.TryGetValue(path, out old))
            {
                if (old.Identity == identity && old.Hash == sha256 && old.Kind == kind && old.SessionId == claim.SessionId) return;
                throw new InvalidOperationException("A different first ownership claim already exists: " + path);
            }
            entries.Add(path, claim);
            try { Persist(); }
            catch { entries.Remove(path); throw; }
        }

        public void Forget(string path, string identity = null)
        {
            lock (gate) ForgetCore(path, identity);
        }

        // Only the current session may refresh its working copy after a verified owned save.
        public void RefreshWorking(string path, string identity, string sha256)
        {
            lock (gate)
            {
                EnsureValid();
                path = Normalize(path);
                var claim = new Claim { Path = path, Identity = identity, Hash = sha256, Kind = "working", SessionId = SessionId };
                Validate(claim);
                RejectReparse(path);
                Claim old;
                bool exists = entries.TryGetValue(path, out old);
                if (exists && (old.Kind != "working" || old.SessionId != SessionId))
                    throw new InvalidOperationException("Only a current-session working claim may be refreshed: " + path);
                if (exists && old.Identity == identity && old.Hash == sha256) return;
                entries[path] = claim;
                try { Persist(); }
                catch
                {
                    if (exists) entries[path] = old;
                    else entries.Remove(path);
                    throw;
                }
            }
        }

        private void ForgetCore(string path, string identity)
        {
            EnsureValid();
            path = Normalize(path);
            Claim old;
            if (!entries.TryGetValue(path, out old) || (identity != null && old.Identity != identity)) return;
            entries.Remove(path);
            try { Persist(); }
            catch { entries.Add(path, old); throw; }
        }

        public void Recover()
        {
            lock (gate) RecoverCore();
        }

        private void RecoverCore()
        {
            if (invalid) { log("Recovery skipped: journal is invalid or unsafe."); return; }
            // Refuse recovery if the journal path has become redirected since construction.
            try { RejectReparse(journalPath); }
            catch (Exception ex) when (Recoverable(ex)) { log("Recovery deferred: " + ex.Message); return; }
            foreach (var claim in entries.Values.ToArray())
            {
                try
                {
                    Validate(claim);
                    RejectReparse(claim.Path);
                    using (var handle = Open(claim.Path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                    {
                        if (handle.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != 2 && error != 3) throw new Win32Exception(error);
                            log("Recovery claim absent: " + claim.Path);
                        }
                        else
                        {
                            using (var stream = new FileStream(handle, FileAccess.Read))
                            {
                                var actual = ReadFacts(handle, stream);
                                RejectReparse(claim.Path);
                                if (actual.Identity != claim.Identity || actual.Hash != claim.Hash)
                                    log("Recovery preserved changed/replaced file: " + claim.Path);
                                else
                                {
                                    var disposition = new FileDisposition { DeleteFile = true };
                                    if (!SetDisposition(handle, 4, ref disposition, Marshal.SizeOf(disposition)))
                                        throw new Win32Exception(Marshal.GetLastWin32Error());
                                    log("Recovery removed owned " + claim.Kind + ": " + claim.Path);
                                }
                            }
                        }
                    }
                    Forget(claim.Path, claim.Identity);
                }
                catch (Exception ex) when (Recoverable(ex))
                { log("Recovery retained claim for retry " + claim.Path + ": " + ex.Message); }
            }
        }

        private void Load()
        {
            try
            {
                RejectReparse(journalPath);
                if (!File.Exists(journalPath)) return;
                var root = JObject.Parse(File.ReadAllText(journalPath), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (root.Properties().Count() != 2 || root["version"]?.Type != JTokenType.Integer || (int)root["version"] != 1 || root["entries"]?.Type != JTokenType.Array)
                    throw new InvalidDataException("Invalid journal envelope.");
                foreach (var token in (JArray)root["entries"])
                {
                    var item = token as JObject;
                    string[] keys = { "path", "identity", "sha256", "kind", "sessionId" };
                    if (item == null || item.Properties().Count() != keys.Length || keys.Any(k => item[k]?.Type != JTokenType.String))
                        throw new InvalidDataException("Invalid journal claim.");
                    var claim = new Claim { Path = (string)item["path"], Identity = (string)item["identity"], Hash = (string)item["sha256"], Kind = (string)item["kind"], SessionId = (string)item["sessionId"] };
                    Validate(claim);
                    if (entries.ContainsKey(claim.Path)) throw new InvalidDataException("Ambiguous duplicate journal path.");
                    entries.Add(claim.Path, claim);
                }
            }
            catch (Exception ex) when (Recoverable(ex) || ex is InvalidDataException || ex is JsonException || ex is OverflowException)
            { invalid = true; entries.Clear(); log("Invalid recovery journal preserved: " + ex.Message); }
        }

        private void Persist()
        {
            RejectReparse(journalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
            RejectReparse(journalPath);
            var array = new JArray(entries.Values.Select(e => new JObject { ["path"] = e.Path, ["identity"] = e.Identity, ["sha256"] = e.Hash, ["kind"] = e.Kind, ["sessionId"] = e.SessionId }));
            byte[] data = new UTF8Encoding(false).GetBytes(new JObject { ["version"] = 1, ["entries"] = array }.ToString(Formatting.Indented));
            string temp = journalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(data, 0, data.Length); stream.Flush(true); }
                RejectReparse(journalPath);
                if (File.Exists(journalPath)) ReplaceWithRetry(temp);
                else File.Move(temp, journalPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private void ReplaceWithRetry(string temp)
        {
            for (int retry = 0; ; retry++)
            {
                RejectReparse(temp);
                RejectReparse(journalPath);
                try { File.Replace(temp, journalPath, null); return; }
                catch (IOException ex)
                {
                    int error = ex.HResult & 0xffff;
                    bool transient = error == 32 || error == 33 || error == 1175 || error == 1176 || error == 1177;
                    // If Windows consumed the source, its partial state is not safe to retry.
                    if (!transient || retry >= 3 || !File.Exists(temp)) throw;
                    RejectReparse(temp);
                    RejectReparse(journalPath);
                    log("Recovery journal atomic replace retry " + (retry + 1) + " error=" + error);
                    Thread.Sleep(20);
                }
            }
        }

        private void Validate(Claim claim)
        {
            if (!string.Equals(claim.Path, Normalize(claim.Path), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Path, journalPath, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(claim.Identity ?? "", @"^\d+:\d+:\d+$") ||
                claim.Identity.Split(':').Any(p => !uint.TryParse(p, out _)) ||
                !Regex.IsMatch(claim.Hash ?? "", @"^[0-9A-F]{64}$") ||
                (claim.Kind != "asset" && claim.Kind != "output" && claim.Kind != "working") ||
                string.IsNullOrWhiteSpace(claim.SessionId)) throw new InvalidDataException("Unvalidated recovery claim.");
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new InvalidDataException("An absolute ordinary file path is required.");
            string root = Path.GetPathRoot(path);
            if (root == @"\" || root.EndsWith(":", StringComparison.Ordinal) || path.Substring(root.Length).Contains(":"))
                throw new InvalidDataException("A drive-relative path or alternate stream is unsafe.");
            string full = Path.GetFullPath(path);
            if (full.Split(Path.DirectorySeparatorChar).Any(p => p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal)))
                throw new InvalidDataException("Ambiguous file path.");
            return full;
        }

        private static void RejectReparse(string path)
        {
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse path rejected: " + current);
            }
        }

        private static Claim ReadFacts(SafeFileHandle handle, Stream stream)
        {
            FileInformation before, after;
            if (!GetInformation(handle, out before)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((before.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory | (uint)FileAttributes.ReadOnly)) != 0)
                throw new IOException("Unsafe or read-only recovery file.");
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!GetInformation(handle, out after)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (before.Volume != after.Volume || before.IndexHigh != after.IndexHigh || before.IndexLow != after.IndexLow || before.SizeHigh != after.SizeHigh || before.SizeLow != after.SizeLow || before.Written.dwHighDateTime != after.Written.dwHighDateTime || before.Written.dwLowDateTime != after.Written.dwLowDateTime || before.Attributes != after.Attributes)
                throw new IOException("Recovery file changed during observation.");
            return new Claim { Identity = before.Volume + ":" + before.IndexHigh + ":" + before.IndexLow, Hash = hash };
        }

        private void EnsureValid() { if (invalid) throw new InvalidDataException("Unsafe journal cannot be overwritten."); }
        private static bool Recoverable(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is ArgumentException || ex is InvalidOperationException || ex is System.Security.SecurityException;
        private sealed class Claim { internal string Path, Identity, Hash, Kind, SessionId; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] internal bool DeleteFile; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle Open(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        private static extern bool GetInformation(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        private static extern bool SetDisposition(SafeFileHandle handle, int type, ref FileDisposition disposition, int size);
    }
}
