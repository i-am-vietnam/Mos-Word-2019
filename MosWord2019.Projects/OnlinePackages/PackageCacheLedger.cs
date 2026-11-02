using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MosWord2019.Projects.OnlinePackages
{
    // Package-specific write-ahead claims supplement the unchanged general recovery journal.
    // Pending files have a durable native file identity before their first plaintext byte is written.
    internal sealed class PackageCacheLedger : IDisposable
    {
        private const string LedgerName = ".package-ownership.json";
        private readonly string root;
        private readonly string ledgerPath;
        private readonly SessionRecoveryJournal journal;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private FileStream lease;
        private string rootIdentity;
        internal string RecoveryId { get; private set; }
        internal static IDisposable Retain(string root, SessionRecoveryJournal journal)
        {
            var ledger = new PackageCacheLedger(root, journal, true);
            journal.PreserveSession(ledger.RecoveryId);
            return new RetainedLease(ledger, journal);
        }
        private sealed class RetainedLease : IDisposable
        {
            private readonly PackageCacheLedger ledger;
            private readonly SessionRecoveryJournal journal;
            internal RetainedLease(PackageCacheLedger ledger, SessionRecoveryJournal journal) { this.ledger = ledger; this.journal = journal; }
            public void Dispose() { ledger.Dispose(); journal.ReleaseSession(ledger.RecoveryId); }
        }
        private sealed class Entry { internal string Identity, Hash; internal bool Directory; }
        internal PackageCacheLedger(string root, SessionRecoveryJournal journal)
        {
            this.root = Path.GetFullPath(root); this.journal = journal;
            ledgerPath = Path.Combine(root, LedgerName);
            RecoveryId = Guid.NewGuid().ToString("N");
            OnlinePackageCache.CreatePrivateDirectory(root);
            rootIdentity = DirectoryIdentity(root);
            lease = new FileStream(Path.Combine(root, ".package-lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            Persist();
            journal.PreserveSession(RecoveryId);
        }
        private PackageCacheLedger(string root, SessionRecoveryJournal journal, bool recovery)
        {
            this.root = Path.GetFullPath(root); this.journal = journal;
            ledgerPath = Path.Combine(root, LedgerName);
            OnlinePackageCache.RejectReparse(ledgerPath);
            lease = new FileStream(Path.Combine(root, ".package-lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
            var data = JObject.Parse(File.ReadAllText(ledgerPath), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if ((int?)data["version"] != 1 || data["rootIdentity"]?.Type != JTokenType.String || data["recoveryId"]?.Type != JTokenType.String || !(data["entries"] is JArray)) throw new InvalidDataException("Invalid package recovery ledger.");
            rootIdentity = (string)data["rootIdentity"]; RecoveryId = (string)data["recoveryId"];
            Guid recoveryId;
            if (!Guid.TryParseExact(RecoveryId, "N", out recoveryId) || rootIdentity != DirectoryIdentity(root)) throw new InvalidDataException("Package root ownership changed.");
            foreach (JObject item in (JArray)data["entries"])
            {
                string relative = (string)item["path"];
                Resolve(relative);
                if (entries.ContainsKey(relative) || item["identity"]?.Type != JTokenType.String || item["directory"]?.Type != JTokenType.Boolean) throw new InvalidDataException("Invalid package ownership claim.");
                entries.Add(relative, new Entry { Identity = (string)item["identity"], Hash = (string)item["hash"], Directory = (bool)item["directory"] });
            }
            }
            catch { lease.Dispose(); lease = null; throw; }
        }
        internal void CreateDirectory(string path)
        {
            EnsureRoot();
            string relative = Relative(path);
            if (relative.Length == 0) return;
            if (!Directory.Exists(Path.GetDirectoryName(path))) CreateDirectory(Path.GetDirectoryName(path));
            if (entries.ContainsKey(relative)) { if (entries[relative].Identity != DirectoryIdentity(path)) throw new IOException("Package directory replaced."); return; }
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Unclaimed package directory exists.");
            OnlinePackageCache.CreatePrivateDirectory(path);
            entries.Add(relative, new Entry { Identity = DirectoryIdentity(path), Directory = true }); Persist();
        }
        internal void ClaimEmptyFile(string path, FileStream stream)
        {
            EnsureRoot();
            if (stream.Length != 0) throw new IOException("Package claim must precede plaintext.");
            string relative = Relative(path);
            if (entries.ContainsKey(relative)) throw new IOException("Package file already claimed.");
            entries.Add(relative, new Entry { Identity = Identity(stream.SafeFileHandle, false), Directory = false });
            Persist();
        }
        internal void CompleteFile(string path, FileStream stream)
        {
            var entry = entries[Relative(path)];
            if (entry.Identity != Identity(stream.SafeFileHandle, false)) throw new IOException("Package file identity changed.");
            stream.Flush(true); stream.Position = 0;
            using (var sha = SHA256.Create()) entry.Hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            Persist();
        }
        internal void RegisterCompleted(string path)
        {
            var entry = entries[Relative(path)];
            journal.Track(path, entry.Identity, entry.Hash, "asset", RecoveryId);
        }
        internal void Cleanup()
        {
            EnsureRoot();
            bool retry = false;
            foreach (var pair in entries.Where(p => !p.Value.Directory).ToArray())
            {
                try
                {
                    string path = Resolve(pair.Key);
                    using (var handle = Open(path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                    {
                        if (handle.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != 2 && error != 3) throw new Win32Exception(error);
                        }
                        else using (var input = new FileStream(handle, FileAccess.Read))
                        {
                            string identity = Identity(handle, false);
                            // This is immutable application cache, never learner working copies.
                            // Native identity proves ownership even if cached plaintext was modified.
                            if (identity == pair.Value.Identity)
                            {
                                EnsureRoot(); OnlinePackageCache.RejectReparse(path);
                                var disposition = new FileDisposition { DeleteFile = true };
                                if (!SetDisposition(handle, 4, ref disposition, Marshal.SizeOf(disposition))) throw new Win32Exception(Marshal.GetLastWin32Error());
                            }
                        }
                    }
                    journal.Forget(path, pair.Value.Identity);
                    entries.Remove(pair.Key); Persist();
                }
                catch (Exception ex) when (Recoverable(ex)) { retry = true; }
            }
            foreach (var pair in entries.Where(p => p.Value.Directory).OrderByDescending(p => p.Key.Length).ToArray())
            {
                try
                {
                    string path = Resolve(pair.Key);
                    if (Directory.Exists(path))
                    {
                        if (DirectoryIdentity(path) != pair.Value.Identity) { entries.Remove(pair.Key); Persist(); continue; }
                        if (Directory.EnumerateFileSystemEntries(path).Any()) { retry = true; continue; }
                        DeleteDirectory(path, pair.Value.Identity);
                    }
                    entries.Remove(pair.Key); Persist();
                }
                catch (Exception ex) when (Recoverable(ex)) { retry = true; }
            }
            journal.ReleaseSession(RecoveryId);
            if (retry || entries.Count != 0) return;
            EnsureRoot();
            lease.Dispose(); lease = null;
            File.Delete(Path.Combine(root, ".package-lease"));
            File.Delete(ledgerPath);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) DeleteDirectory(root, rootIdentity);
            // The containing online session folder is created by the cache; remove it only if empty.
            string sessionRoot = Path.GetDirectoryName(root);
            if (Directory.Exists(sessionRoot) && !Directory.EnumerateFileSystemEntries(sessionRoot).Any()) Directory.Delete(sessionRoot, false);
        }
        internal static void Recover(string packagesRoot, SessionRecoveryJournal journal)
        {
            OnlinePackageCache.RejectReparse(packagesRoot);
            if (!Directory.Exists(packagesRoot)) return;
            foreach (string session in Directory.GetDirectories(packagesRoot))
            {
                Guid ignored;
                if (!Guid.TryParseExact(Path.GetFileName(session), "N", out ignored)) continue;
                try
                {
                    OnlinePackageCache.RejectReparse(session);
                    foreach (string root in Directory.GetDirectories(session))
                    {
                        if (!Guid.TryParseExact(Path.GetFileName(root), "N", out ignored)) continue;
                        try
                        {
                            OnlinePackageCache.RejectReparse(root);
                            if (!File.Exists(Path.Combine(root, LedgerName))) continue;
                            using (var ledger = new PackageCacheLedger(root, journal, true)) ledger.Cleanup();
                        }
                        catch (Exception ex) when (Recoverable(ex) || ex is JsonException || ex is InvalidCastException) { }
                    }
                }
                catch (Exception ex) when (Recoverable(ex)) { }
            }
        }
        private void EnsureRoot() { if (DirectoryIdentity(root) != rootIdentity) throw new IOException("Package root ownership changed."); }
        private string Relative(string path)
        {
            string full = Path.GetFullPath(path);
            if (full == root) return "";
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Package claim outside owned root.");
            return full.Substring(root.Length + 1);
        }
        private string Resolve(string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(":") || relative.Split('\\', '/').Any(p => p.Length == 0 || p == "." || p == "..")) throw new InvalidDataException("Unsafe package recovery path.");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            Relative(path); OnlinePackageCache.RejectReparse(path); return path;
        }
        private void Persist()
        {
            EnsureRoot();
            var data = new JObject { ["version"] = 1, ["rootIdentity"] = rootIdentity, ["recoveryId"] = RecoveryId,
                ["entries"] = new JArray(entries.Select(p => new JObject { ["path"] = p.Key, ["identity"] = p.Value.Identity, ["hash"] = p.Value.Hash, ["directory"] = p.Value.Directory })) };
            byte[] bytes = Encoding.UTF8.GetBytes(data.ToString(Formatting.None));
            string temp = ledgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            OnlinePackageCache.RejectReparse(temp);
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                MosWord2019.Core.IO.AtomicFile.Commit(temp, ledgerPath, File.Exists(ledgerPath), EnsureRoot);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static void DeleteDirectory(string path, string expectedIdentity)
        {
            OnlinePackageCache.RejectReparse(path);
            using (var handle = Open(path, 0x80010000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (Identity(handle, true) != expectedIdentity) throw new IOException("Package directory replaced before deletion.");
                var disposition = new FileDisposition { DeleteFile = true };
                if (!SetDisposition(handle, 4, ref disposition, Marshal.SizeOf(disposition))) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        private static string DirectoryIdentity(string path)
        {
            OnlinePackageCache.RejectReparse(path);
            var security = Directory.GetAccessControl(path);
            var user = WindowsIdentity.GetCurrent().User;
            if (!security.AreAccessRulesProtected || !security.GetOwner(typeof(SecurityIdentifier)).Equals(user) ||
                security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r => r.AccessControlType == AccessControlType.Allow && !r.IdentityReference.Equals(user))) throw new IOException("Package directory is not private.");
            using (var handle = Open(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            { if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); return Identity(handle, true); }
        }
        private static string Identity(SafeFileHandle handle, bool directory)
        {
            FileInformation info;
            if (!GetInformation(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory) throw new IOException("Unsafe package identity.");
            return info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow;
        }
        private static bool Recoverable(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is ArgumentException || ex is InvalidOperationException || ex is System.Security.SecurityException;
        public void Dispose() { if (lease != null) { lease.Dispose(); lease = null; } }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        { internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written; internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] internal bool DeleteFile; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle Open(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)] private static extern bool GetInformation(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] private static extern bool SetDisposition(SafeFileHandle handle, int type, ref FileDisposition disposition, int size);
    }
}
