using Microsoft.Win32.SafeHandles;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MosWord2019.Projects
{
    public sealed partial class TrainingWorkspaceService
    {
        // This ledger lives only as long as this Training session. No ownership is inferred
        // from a filename on disk, including after a crash or in another Trainer instance.
        private readonly Dictionary<string, List<StagedAsset>> stagedAssets =
            new Dictionary<string, List<StagedAsset>>(StringComparer.OrdinalIgnoreCase);

        public static void ValidateAssetOptions(ProjectPackage package)
        {
            if (package == null || package.Meta == null || string.IsNullOrWhiteSpace(package.ProjectFolderPath))
                throw new InvalidDataException("Asset staging requires a valid package.");
            var options = package.Meta.AssetStaging;
            if (options == null) return; // Preserve the existing Documents-only package contract.
            if (options.CleanupPolicy != "OnProjectClose" || options.AdditionalCopies == null)
                throw new InvalidDataException("Unsupported asset staging policy.");
            var copies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var copy in options.AdditionalCopies)
            {
                if (copy == null || !SafeAssetName(copy.Source) || copy.Destination != "Pictures" ||
                    !copies.Add(copy.Destination + "/" + copy.Source))
                    throw new InvalidDataException("Asset copies require unique top-level filenames and the Pictures destination.");
                string source = Path.Combine(package.ProjectFolderPath, "assets", copy.Source);
                if (!File.Exists(source)) throw new InvalidDataException("Declared asset does not exist: " + copy.Source);
            }
        }

        private IList<string> StageAssets(ProjectPackage package)
        {
            ValidateAssetOptions(package);
            string key = Path.GetFullPath(package.ProjectFolderPath);
            string assetsRoot = Path.Combine(key, "assets");
            var available = new List<string>();
            if (!Directory.Exists(assetsRoot)) return available;
            RejectReparsePath(assetsRoot);
            var options = package.Meta.AssetStaging;
            var requests = new List<Tuple<string, string>>();
            if (options == null || options.CopyAssetsToDocuments)
                requests.AddRange(Directory.GetFiles(assetsRoot, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => Tuple.Create(p, documentsRootPath)));
            if (options != null)
                requests.AddRange(options.AdditionalCopies.Select(copy =>
                    Tuple.Create(Path.Combine(assetsRoot, copy.Source), picturesRootPath)));

            List<StagedAsset> ledger;
            if (!stagedAssets.TryGetValue(key, out ledger)) stagedAssets[key] = ledger = new List<StagedAsset>();
            var operation = new List<StagedAsset>();
            try
            {
                foreach (var request in requests)
                {
                    string source = Path.GetFullPath(request.Item1), root = request.Item2;
                    string name = Path.GetFileName(source);
                    if (!SafeAssetName(name)) throw new InvalidDataException("Invalid asset filename.");
                    EnsureContained(assetsRoot, source, "Asset source escapes its package.");
                    RejectReparsePath(source);
                    RejectReparsePath(root);
                    Directory.CreateDirectory(root);
                    string destination = Path.Combine(root, name);
                    EnsureContained(root, destination, "Asset destination escapes its approved root.");
                    RejectReparsePath(destination);
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        string hash = AssetHash(input);
                        if (File.Exists(destination))
                        {
                            using (var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                                if (AssetHash(existing) != hash) throw new ProjectAssetConflictException(name, root);
                            if (!ledger.Any(e => e.Destination == destination))
                                ledger.Add(new StagedAsset { Source = source, Destination = destination, Hash = hash, Created = false });
                            AppLogger.Write("Training asset reused " + destination + " source=" + source + " sha256=" + hash);
                        }
                        else
                        {
                            // CreateNew is essential: an intervening personal file can never be overwritten.
                            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                            {
                                var entry = new StagedAsset { Source = source, Destination = destination, Root = root, Hash = hash,
                                    Created = true, Cleanup = options != null, Identity = AssetIdentity(output.SafeFileHandle) };
                                operation.Add(entry); ledger.Add(entry);
                                try { input.Position = 0; input.CopyTo(output); output.Flush(); }
                                catch { entry.Hash = AssetHash(output); throw; }
                                if (AssetHash(output) != hash) throw new IOException("Asset copy hash mismatch: " + name);
                            }
                            AppLogger.Write("Training asset created " + destination + " source=" + source + " sha256=" + hash);
                        }
                    }
                    available.Add(destination);
                }
                return available;
            }
            catch
            {
                // Roll back only this attempt, never copies owned by an already active session.
                foreach (var entry in operation) CleanupAsset(entry, null);
                ledger.RemoveAll(e => operation.Contains(e));
                throw;
            }
        }

        public void CleanupProjectAssets(ProjectPackage package, Action<string> log = null)
        {
            if (package == null) return;
            string key = Path.GetFullPath(package.ProjectFolderPath);
            List<StagedAsset> ledger;
            if (!stagedAssets.TryGetValue(key, out ledger)) return;
            foreach (var entry in ledger.Where(e => e.Created && e.Cleanup)) CleanupAsset(entry, log);
            stagedAssets.Remove(key);
        }

        private static void CleanupAsset(StagedAsset entry, Action<string> log)
        {
            log = log ?? (message => AppLogger.Write(message));
            try
            {
                EnsureContained(entry.Root, entry.Destination, "Asset cleanup escapes its approved root.");
                RejectReparsePath(entry.Destination);
                if (!File.Exists(entry.Destination)) return;
                // Hash and delete the same exclusive file handle. Replacing the path or modifying
                // bytes between verification and deletion cannot delete a different personal file.
                using (var handle = OpenAssetForDeletion(entry.Destination, 0x80010000, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    using (var file = new FileStream(handle, FileAccess.Read))
                    {
                        if (AssetIdentity(handle) != entry.Identity || AssetHash(file) != entry.Hash)
                        { log?.Invoke("Preserved changed/replaced staged asset " + entry.Destination); return; }
                        var disposition = new FileDisposition { DeleteFile = true };
                        if (!SetAssetDisposition(handle, 4, ref disposition, Marshal.SizeOf(disposition)))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
                log?.Invoke("Removed owned staged asset " + entry.Destination + " source=" + entry.Source + " sha256=" + entry.Hash);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is InvalidOperationException)
            { log?.Invoke("Preserved staged asset; cleanup failed " + entry.Destination + ": " + ex.Message); }
        }

        private static bool SafeAssetName(string name)
        {
            string stem = Path.GetFileNameWithoutExtension(name ?? "").ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(stem) ||
                System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$")) return false;
            return !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." && !Path.IsPathRooted(name) &&
                name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                !name.EndsWith(".", StringComparison.Ordinal) && !name.EndsWith(" ", StringComparison.Ordinal);
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
                    throw new InvalidOperationException("Asset paths must not traverse a reparse point: " + current);
            }
        }

        private static string AssetHash(Stream stream)
        {
            stream.Position = 0;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static string AssetIdentity(SafeFileHandle handle)
        {
            FileIdentity info;
            if (!GetAssetInformation(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow;
        }

        private sealed class StagedAsset
        {
            internal string Source, Destination, Root, Hash, Identity;
            internal bool Created, Cleanup;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdentity
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] internal bool DeleteFile; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenAssetForDeletion(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        private static extern bool GetAssetInformation(SafeFileHandle handle, out FileIdentity info);
        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        private static extern bool SetAssetDisposition(SafeFileHandle handle, int type, ref FileDisposition disposition, int size);
    }
}
