using Microsoft.Win32.SafeHandles;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MosWord2019.Projects
{
    /// <summary>Session-only facts and explicit ownership of exact declared output paths.</summary>
    public sealed class OutputOwnershipLedger
    {
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Action<string> log;
        private readonly SessionRecoveryJournal journal;

        public OutputOwnershipLedger(ProjectPackage package, string documentsFolder, string templateFolder,
            Action<string> log = null, string sessionId = null, SessionRecoveryJournal journal = null)
        {
            if (package == null || package.Tasks == null) throw new ArgumentNullException(nameof(package));
            DocumentsFolder = AbsoluteFolder(documentsFolder);
            SessionId = sessionId ?? Guid.NewGuid().ToString("N");
            SessionStartedUtc = DateTime.UtcNow;
            this.log = log ?? (message => AppLogger.Write(message));
            this.journal = journal;
            foreach (var task in package.Tasks)
            {
                bool template = task.AssertionType == TemplateOutputLocation.AssertionType || task.AssertionType == "SavedWordTemplate";
                bool documents = task.AssertionType == "FileExistsInDocuments" || task.AssertionType == "PlainTextDocumentExport";
                if (!template && !documents) continue;
                if (template && TemplateFolder == null) TemplateFolder = AbsoluteFolder(templateFolder);
                JToken token;
                if (task.Extra == null || !task.Extra.TryGetValue("expectedFileName", out token) || token == null || token.Type != JTokenType.String)
                    throw new InvalidDataException("Missing declared output filename.");
                string name = (string)token;
                ValidateName(name);
                string path = template ? TemplateOutputLocation.ResolveInFolder(name, TemplateFolder) : Path.Combine(DocumentsFolder, name);
                path = Path.GetFullPath(path);
                if (!entries.ContainsKey(path)) entries.Add(path, new Entry { Path = path, Baseline = Read(path) });
            }
        }

        public string SessionId { get; }
        public DateTime SessionStartedUtc { get; private set; }
        public string DocumentsFolder { get; }
        public string TemplateFolder { get; }

        internal static OutputOwnershipLedger Restore(ProjectPackage package, OutputOwnershipSnapshot snapshot, string documents,
            string templates, string sessionId, SessionRecoveryJournal journal, Action<string> log)
        {
            if (snapshot == null || snapshot.SessionId != sessionId || !string.Equals(Path.GetFullPath(documents), snapshot.DocumentsFolder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid recovered output baseline.");
            var ledger = new OutputOwnershipLedger(package, documents, templates, log, sessionId, journal);
            if (snapshot.Outputs.Count != ledger.entries.Count) throw new InvalidDataException("Recovered output declarations differ.");
            foreach (var output in snapshot.Outputs)
            {
                Entry entry;
                if (output.Baseline == null || !ledger.entries.TryGetValue(output.Baseline.Path, out entry)) throw new InvalidDataException("Unexpected recovered output path.");
                entry.Baseline = output.Baseline;
                var claim = journal.OutputClaim(entry.Path, sessionId);
                if (claim != null) entry.Claim = new OutputFileState(entry.Path, true, 0, claim.Item2, DateTime.MinValue, claim.Item1, DateTime.UtcNow);
            }
            ledger.SessionStartedUtc = snapshot.SessionStartedUtc; return ledger;
        }
        public OutputOwnershipSnapshot Capture(string ownedDocumentPath = null)
        {
            if (!string.IsNullOrWhiteSpace(ownedDocumentPath)) ObserveOwnedDocument(ownedDocumentPath);
            var outputs = new List<DeclaredOutputSnapshot>();
            foreach (var entry in entries.Values)
            {
                try { outputs.Add(new DeclaredOutputSnapshot(entry.Baseline, Read(entry.Path))); }
                catch (Exception ex) when (Recoverable(ex))
                { outputs.Add(new DeclaredOutputSnapshot(entry.Baseline, null, ex.GetType().Name + ": " + ex.Message)); }
            }
            return new OutputOwnershipSnapshot(SessionId, SessionStartedUtc, DocumentsFolder, TemplateFolder, outputs);
        }

        // The caller supplies only the path reported by its verified owned Word document.
        public void ObserveOwnedDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Entry entry;
            if (!entries.TryGetValue(Path.GetFullPath(path), out entry) || entry.Baseline.Exists || entry.Claim != null) return;
            try
            {
                var observed = Read(entry.Path);
                if (observed.Exists)
                {
                    Claim(entry, observed);
                }
            }
            catch (Exception ex) when (Recoverable(ex))
            { log("Preserved output; ownership observation failed " + entry.Path + ": " + ex.Message); }
        }

        public void Cleanup(bool ownedDocumentClosed)
        {
            foreach (var entry in entries.Values)
            {
                if (!ownedDocumentClosed)
                { log("Preserved output; cleanup deferred while owned Word is live " + entry.Path); continue; }
                try
                {
                    if (!entry.Baseline.Exists && entry.Claim == null)
                    {
                        var observed = Read(entry.Path);
                        if (observed.Exists && observed.CreatedUtc >= entry.Baseline.ObservedUtc)
                            Claim(entry, observed);
                    }
                    if (entry.Baseline.Exists || entry.Claim == null)
                    { log("Preserved pre-existing or unclaimed output " + entry.Path); continue; }
                    RejectReparsePath(entry.Path);
                    using (var handle = Open(entry.Path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                    {
                        if (handle.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == 2 || error == 3)
                            { journal?.Forget(entry.Path, entry.Claim.Identity); log("Owned output already absent " + entry.Path); continue; }
                            throw new Win32Exception(error);
                        }
                        using (var stream = new FileStream(handle, FileAccess.Read))
                        {
                            var state = ReadHandle(entry.Path, handle, stream, true);
                            RejectReparsePath(entry.Path);
                            if (state.Identity != entry.Claim.Identity || state.Sha256 != entry.Claim.Sha256)
                            { journal?.Forget(entry.Path, entry.Claim.Identity); log("Preserved changed/replaced output " + entry.Path); continue; }
                            var disposition = new FileDisposition { DeleteFile = true };
                            if (!SetDisposition(handle, 4, ref disposition, Marshal.SizeOf(disposition)))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                    }
                    journal?.Forget(entry.Path, entry.Claim.Identity);
                    log("Removed owned output " + entry.Path + " session=" + SessionId);
                }
                catch (Exception ex) when (Recoverable(ex))
                { log("Preserved output; cleanup failed " + entry.Path + ": " + ex.Message); }
            }
        }

        private void Claim(Entry entry, OutputFileState observed)
        {
            if (entry.Baseline.Exists || entry.Claim != null) return;
            // This attempt proved absence before Word opened, so a prior attempt's exact-path claim is obsolete.
            journal?.Forget(entry.Path);
            // Persist the first claim before cleanup; later edits must never refresh its fingerprint.
            journal?.Track(entry.Path, observed.Identity, observed.Sha256, "output", SessionId);
            entry.Claim = observed;
            log("Training output claimed " + entry.Path + " session=" + SessionId);
        }

        private static OutputFileState Read(string path)
        {
            RejectReparsePath(path);
            using (var handle = Open(path, 0x80000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 2 || error == 3) return new OutputFileState(path, false, 0, null, DateTime.MinValue, null, DateTime.UtcNow);
                    throw new Win32Exception(error);
                }
                using (var stream = new FileStream(handle, FileAccess.Read))
                {
                    var state = ReadHandle(path, handle, stream, false);
                    RejectReparsePath(path);
                    return state;
                }
            }
        }

        private static OutputFileState ReadHandle(string path, SafeFileHandle handle, Stream stream, bool deleting)
        {
            FileInformation info;
            if (!GetInformation(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0 ||
                (deleting && (info.Attributes & (uint)FileAttributes.ReadOnly) != 0))
                throw new IOException("Output is redirected, a directory, or read-only during cleanup.");
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            FileInformation after;
            if (!GetInformation(handle, out after)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (info.Volume != after.Volume || info.IndexHigh != after.IndexHigh || info.IndexLow != after.IndexLow ||
                info.SizeHigh != after.SizeHigh || info.SizeLow != after.SizeLow ||
                info.Written.dwHighDateTime != after.Written.dwHighDateTime || info.Written.dwLowDateTime != after.Written.dwLowDateTime ||
                info.Attributes != after.Attributes)
                throw new IOException("Output changed during observation: " + path);
            long written = ((long)info.Written.dwHighDateTime << 32) | (uint)info.Written.dwLowDateTime;
            long created = ((long)info.Created.dwHighDateTime << 32) | (uint)info.Created.dwLowDateTime;
            return new OutputFileState(path, true, ((long)info.SizeHigh << 32) | info.SizeLow, hash,
                DateTime.FromFileTimeUtc(written), info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow, DateTime.UtcNow,
                creationUtc: DateTime.FromFileTimeUtc(created));
        }

        private static string AbsoluteFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidDataException("An absolute output folder is required.");
            string root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root) || root == @"\" || root.EndsWith(":", StringComparison.Ordinal))
                throw new InvalidDataException("An absolute output folder is required.");
            return Path.GetFullPath(folder);
        }

        private static void ValidateName(string name)
        {
            string stem = (name ?? "").Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..") ||
                name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal) ||
                stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CLOCK$" ||
                Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$"))
                throw new InvalidDataException("A safe single declared output filename is required.");
        }

        private static void RejectReparsePath(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Output path traverses a reparse point: " + current);
            }
        }

        private static bool Recoverable(Exception ex) => ex is IOException || ex is UnauthorizedAccessException ||
            ex is Win32Exception || ex is ArgumentException || ex is InvalidOperationException || ex is System.Security.SecurityException;

        private sealed class Entry { internal string Path; internal OutputFileState Baseline, Claim; }
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
