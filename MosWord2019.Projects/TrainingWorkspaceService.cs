using MosWord2019.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MosWord2019.Core.Diagnostics;

namespace MosWord2019.Projects
{
    public sealed class ProjectAssetConflictException : IOException
    {
        public ProjectAssetConflictException(string assetFileName)
            : this(assetFileName, "Documents")
        {
        }

        public ProjectAssetConflictException(string assetFileName, string location)
            : base(location + " already contains a different file named " + assetFileName + ". Move or rename that file, then try again.")
        {
            AssetFileName = assetFileName ?? "";
            Location = location ?? "";
        }

        public string AssetFileName { get; private set; }
        public string Location { get; private set; }
    }

    public sealed partial class TrainingWorkspaceService
    {
        private static readonly Regex ProjectIdPattern =
            new Regex(@"^Word2019_P\d{2,}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> SupportedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".doc" };
        private readonly string workingRootPath;
        private readonly string documentsRootPath;
        private readonly string picturesRootPath;
        private readonly SessionRecoveryJournal journal;
        private readonly string journalSessionId;
        private readonly object workingGate = new object();
        private readonly Dictionary<string, StagedAsset> workingFiles = new Dictionary<string, StagedAsset>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> workingDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TrainingWorkspaceService()
            : this(DefaultWorkingRoot(), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        {
        }

        public TrainingWorkspaceService(SessionRecoveryJournal journal)
            : this(DefaultWorkingRoot(), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), journal)
        {
        }

        public TrainingWorkspaceService(string workingRootPath)
            : this(workingRootPath, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        {
        }

        public TrainingWorkspaceService(string workingRootPath, string documentsRootPath)
            : this(workingRootPath, documentsRootPath, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
        {
        }

        public TrainingWorkspaceService(string workingRootPath, string documentsRootPath, string picturesRootPath, SessionRecoveryJournal journal = null, string journalSessionId = null)
        {
            if (string.IsNullOrWhiteSpace(workingRootPath))
                throw new ArgumentException("A Training working root is required.", nameof(workingRootPath));
            if (string.IsNullOrWhiteSpace(documentsRootPath))
                throw new ArgumentException("A Documents root is required.", nameof(documentsRootPath));
            this.workingRootPath = Path.GetFullPath(workingRootPath);
            this.documentsRootPath = Path.GetFullPath(documentsRootPath);
            if (string.IsNullOrWhiteSpace(picturesRootPath))
                throw new ArgumentException("A Pictures root is required.", nameof(picturesRootPath));
            this.picturesRootPath = Path.GetFullPath(picturesRootPath);
            this.journal = journal;
            this.journalSessionId = journalSessionId;
        }

        public string WorkingRootPath { get { return workingRootPath; } }
        public string DocumentsRootPath { get { return documentsRootPath; } }
        public string PicturesRootPath { get { return picturesRootPath; } }

        public IList<string> StageProjectAssetsToDocuments(ProjectPackage package)
        {
            return StageAssets(package);
        }

        public string GetWorkingCopyPath(ProjectPackage package)
        {
            PackagePaths paths = GetPackagePaths(package);
            return paths.WorkingPath;
        }

        public string PrepareWorkingCopy(ProjectPackage package)
        {
            return PrepareFreshWorkingCopy(package);
        }

        /// <summary>Begins a new Training session. The caller must close its owned Word document first.</summary>
        public string PrepareFreshWorkingCopy(ProjectPackage package) { return ResetWorkingCopy(package); }

        /// <summary>
        /// Restores the closed working file and removes exact reserved alternate/checkpoint files.
        /// The caller must close its owned Word document first.
        /// </summary>
        public string ResetWorkingCopy(ProjectPackage package)
        {
            PackagePaths paths = GetPackagePaths(package);
            RejectReparsePath(paths.StarterPath);
            RejectReparsePath(paths.WorkingDirectory);
            Directory.CreateDirectory(paths.WorkingDirectory);
            RejectReparsePath(paths.WorkingDirectory);
            // Invalidate these exact reserved workspace states at every session boundary.
            // External exports and arbitrary files in the directory are never cleanup targets.
            string[] ownedPaths = { Path.Combine(paths.WorkingDirectory, "work.doc"),
                Path.Combine(paths.WorkingDirectory, "work.docx"), Path.Combine(paths.WorkingDirectory, "work.docm"),
                Path.Combine(paths.WorkingDirectory, "export-checkpoint.docx") };
            var guards = new List<FileStream>();
            string temporaryPath = Path.Combine(
                paths.WorkingDirectory,
                ".reset-" + Guid.NewGuid().ToString("N") + paths.Extension);
            try
            {
                foreach (string owned in ownedPaths)
                {
                    EnsureContained(workingRootPath, owned, "Working state escapes the Training root.");
                    if (string.Equals(paths.StarterPath, owned, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("A Training working path cannot be the package starter: " + owned);
                    RejectReparsePath(owned);
                    if (Directory.Exists(owned)) throw new IOException("A working file path is a directory: " + owned);
                    // Keep guards until replacement/deletion. ShareDelete permits our atomic
                    // rename but refuses any existing or newly opening reader/writer (including Word).
                    if (File.Exists(owned)) guards.Add(new FileStream(owned, FileMode.Open, FileAccess.Read, FileShare.Delete));
                }
                using (var input = File.OpenRead(paths.StarterPath))
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    try { input.CopyTo(output); output.Flush(true); }
                    finally { RecordWorkingFile(temporaryPath, output); }
                }
                MakeWritable(temporaryPath);
                MosWord2019.Core.IO.AtomicFile.Commit(temporaryPath, paths.WorkingPath, File.Exists(paths.WorkingPath),
                    () => RejectReparsePath(paths.WorkingPath), ignoreMetadataErrors: true);
                MakeWritable(paths.WorkingPath);
                MoveWorkingRecord(temporaryPath, paths.WorkingPath);
                foreach (string owned in ownedPaths.Where(p => !string.Equals(p, paths.WorkingPath, StringComparison.OrdinalIgnoreCase)))
                    if (File.Exists(owned)) { File.Delete(owned); ForgetWorkingFile(owned); }
                return paths.WorkingPath;
            }
            catch (Exception ex)
            {
                AppLogger.Write("Training fresh working copy failed " + paths.WorkingPath, ex);
                throw new IOException("Unable to restore a fresh Training starter at " + paths.WorkingPath + ". Close documents using its workspace and retry.", ex);
            }
            finally
            {
                foreach (var guard in guards) guard.Dispose();
                if (File.Exists(temporaryPath)) { File.Delete(temporaryPath); ForgetWorkingFile(temporaryPath); }
            }
        }

        private PackagePaths GetPackagePaths(ProjectPackage package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (package.Meta == null) throw new ArgumentException("Package metadata is required.", nameof(package));
            string projectId = (package.Meta.ProjectId ?? "").Trim();
            if (!ProjectIdPattern.IsMatch(projectId))
                throw new ArgumentException("ProjectId must match Word2019_P followed by at least two digits.", nameof(package));
            if (string.IsNullOrWhiteSpace(package.ProjectFolderPath))
                throw new ArgumentException("ProjectFolderPath is required.", nameof(package));

            string packageRoot = Path.GetFullPath(package.ProjectFolderPath);
            string starterName = (package.Meta.Starter ?? "").Trim();
            if (string.IsNullOrWhiteSpace(starterName) ||
                Path.IsPathRooted(starterName) ||
                !string.Equals(starterName, Path.GetFileName(starterName), StringComparison.Ordinal))
                throw new ArgumentException("Starter must be a file name in the package root.", nameof(package));
            string extension = Path.GetExtension(starterName);
            if (!SupportedExtensions.Contains(extension))
                throw new NotSupportedException("Training working copies support .docx, .docm and .doc starters.");

            string starterPath = Path.GetFullPath(Path.Combine(packageRoot, starterName));
            EnsureContained(packageRoot, starterPath, "Starter path escapes the package root.");
            if (!File.Exists(starterPath)) throw new FileNotFoundException("Starter document does not exist.", starterPath);

            string workingDirectory = Path.GetFullPath(Path.Combine(workingRootPath, projectId));
            lock (workingGate) workingDirectories.Add(workingDirectory);
            EnsureContained(workingRootPath, workingDirectory, "Working path escapes the Training root.");
            return new PackagePaths
            {
                StarterPath = starterPath,
                WorkingDirectory = workingDirectory,
                WorkingPath = Path.Combine(workingDirectory, "work" + extension.ToLowerInvariant()),
                Extension = extension.ToLowerInvariant()
            };
        }

        private void RecordWorkingFile(string path, FileStream file)
        {
            lock (workingGate)
            {
                FileIdentity before, after;
                if (!GetAssetInformation(file.SafeFileHandle, out before))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                if ((before.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Working observations cannot claim a reparse point: " + path);
                var entry = new StagedAsset { Destination = path, Root = workingRootPath, Created = true,
                    Identity = AssetIdentity(file.SafeFileHandle), Hash = AssetHash(file) };
                if (!GetAssetInformation(file.SafeFileHandle, out after))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                if (before.Volume != after.Volume || before.IndexHigh != after.IndexHigh || before.IndexLow != after.IndexLow ||
                    before.Attributes != after.Attributes || before.SizeHigh != after.SizeHigh || before.SizeLow != after.SizeLow ||
                    before.Written.dwHighDateTime != after.Written.dwHighDateTime || before.Written.dwLowDateTime != after.Written.dwLowDateTime)
                    throw new IOException("Working file changed during observation: " + path);
                // This observation follows an owned save or a file creation under our handle.
                journal?.RefreshWorking(path, entry.Identity, entry.Hash, journalSessionId);
                workingFiles[path] = entry;
            }
        }

        private void MoveWorkingRecord(string source, string destination)
        {
            lock (workingGate)
            {
                StagedAsset entry;
                if (!workingFiles.TryGetValue(source, out entry)) return;
                // The caller has just atomically moved our newly created file over this
                // reserved path. A retained older-session claim no longer names its file.
                // Passive observations must continue to reject older-session claims.
                journal?.Forget(destination);
                journal?.RefreshWorking(destination, entry.Identity, entry.Hash, journalSessionId);
                ForgetWorkingFile(source);
                entry.Destination = destination;
                workingFiles[destination] = entry;
            }
        }

        private void ForgetWorkingFile(string path)
        {
            lock (workingGate)
            {
                StagedAsset entry;
                if (workingFiles.TryGetValue(path, out entry)) journal?.Forget(path, entry.Identity);
                workingFiles.Remove(path);
            }
        }

        /// <summary>Observe reserved files only after an owned Word save/close.</summary>
        public void RecordOwnedWorkingFile(string path)
        {
            lock (workingGate)
            {
                path = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(path);
                if (!workingDirectories.Contains(directory) || !IsReservedWorkingFile(path))
                    throw new InvalidOperationException("An owned working observation must be a reserved file in the active workspace.");
                RejectReparsePath(path);
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) RecordWorkingFile(path, file);
            }
        }

        /// <summary>An exact owned path may prove an atomic Word save; otherwise refresh only unchanged file identities.</summary>
        public void ObserveWorkingFiles(string ownedDocumentPath = null)
        {
            lock (workingGate)
            {
                if (!string.IsNullOrWhiteSpace(ownedDocumentPath))
                {
                    try
                    {
                        string path = Path.GetFullPath(ownedDocumentPath);
                        if (workingDirectories.Contains(Path.GetDirectoryName(path)) && IsReservedWorkingFile(path)) RecordOwnedWorkingFile(path);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                    { AppLogger.Write("Owned working observation retained file " + ownedDocumentPath, ex); }
                    return;
                }
                foreach (var pair in workingFiles.ToArray())
                {
                    string path = pair.Key;
                    try
                    {
                        RejectReparsePath(path);
                        if (!File.Exists(path)) continue;
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (AssetIdentity(file.SafeFileHandle) != pair.Value.Identity)
                            { AppLogger.Write("Preserved replaced working file during observation " + path); continue; }
                            RecordWorkingFile(path, file);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                    { AppLogger.Write("Working observation retained file " + path, ex); }
                }
            }
        }

        /// <summary>Normal exit only, after successfully closing owned Word. Never used for crash recovery.</summary>
        public void CleanupWorkingFiles(Action<string> log = null)
        {
            lock (workingGate)
            {
                log = log ?? (message => AppLogger.Write(message));
                try
                {
                    RejectReparsePath(workingRootPath);
                    if (!Directory.Exists(workingRootPath)) return;
                    foreach (string directory in Directory.GetDirectories(workingRootPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (!ProjectIdPattern.IsMatch(Path.GetFileName(directory))) continue;
                        try
                        {
                            RejectReparsePath(directory);
                            foreach (string path in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).Where(IsReservedWorkingFile)) CleanupWorkingFile(path, log);
                            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                        { log("Preserved working directory " + directory + ": " + ex.Message); }
                    }
                    if (!Directory.EnumerateFileSystemEntries(workingRootPath).Any()) Directory.Delete(workingRootPath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                { log("Preserved Working root: " + ex.Message); }
            }
        }

        private void CleanupWorkingFile(string path, Action<string> log)
        {
            try
            {
                RejectReparsePath(path);
                using (var handle = OpenAssetForDeletion(path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    using (var file = new FileStream(handle, FileAccess.Read))
                    {
                        StagedAsset entry;
                        if (workingFiles.TryGetValue(path, out entry) && entry.Identity != AssetIdentity(handle))
                        { journal?.Forget(path, entry.Identity); log("Preserved replaced working file " + path); return; }
                        var disposition = new FileDisposition { DeleteFile = true };
                        if (!SetAssetDisposition(handle, 4, ref disposition, System.Runtime.InteropServices.Marshal.SizeOf(disposition)))
                            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                    }
                }
                ForgetWorkingFile(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            { log("Preserved working file; cleanup failed " + path + ": " + ex.Message); }
        }

        private static bool IsReservedWorkingFile(string path)
        {
            string name = Path.GetFileName(path);
            return new[] { "work.doc", "work.docx", "work.docm", "export-checkpoint.docx" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                Regex.IsMatch(name, @"^\.reset-[a-fA-F0-9]{32}\.(doc|docx|docm)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ||
                Regex.IsMatch(name, @"^\.(saveas|export)-[a-fA-F0-9]{32}\.docx$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        private static void EnsureContained(string root, string child, string message)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(message);
        }

        private static void MakeWritable(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        private static string DefaultWorkingRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MosWord2019", "Working");
        }

        private static bool FilesEqual(string left, string right)
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length) return false;
            using (var algorithm = SHA256.Create())
            using (var leftStream = File.OpenRead(left))
            using (var rightStream = File.OpenRead(right))
            {
                return algorithm.ComputeHash(leftStream).SequenceEqual(algorithm.ComputeHash(rightStream));
            }
        }

        private sealed class PackagePaths
        {
            internal string StarterPath;
            internal string WorkingDirectory;
            internal string WorkingPath;
            internal string Extension;
        }
    }
}
