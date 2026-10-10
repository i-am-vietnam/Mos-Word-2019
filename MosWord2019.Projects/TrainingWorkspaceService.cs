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

        public TrainingWorkspaceService()
            : this(DefaultWorkingRoot(), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
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

        public TrainingWorkspaceService(string workingRootPath, string documentsRootPath, string picturesRootPath)
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
                File.Copy(paths.StarterPath, temporaryPath, false);
                MakeWritable(temporaryPath);
                if (File.Exists(paths.WorkingPath))
                    File.Replace(temporaryPath, paths.WorkingPath, null, true);
                else
                    File.Move(temporaryPath, paths.WorkingPath);
                MakeWritable(paths.WorkingPath);
                foreach (string owned in ownedPaths.Where(p => !string.Equals(p, paths.WorkingPath, StringComparison.OrdinalIgnoreCase)))
                    if (File.Exists(owned)) File.Delete(owned);
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
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
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
            EnsureContained(workingRootPath, workingDirectory, "Working path escapes the Training root.");
            return new PackagePaths
            {
                StarterPath = starterPath,
                WorkingDirectory = workingDirectory,
                WorkingPath = Path.Combine(workingDirectory, "work" + extension.ToLowerInvariant()),
                Extension = extension.ToLowerInvariant()
            };
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
