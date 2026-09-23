using MosWord2019.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MosWord2019.Projects
{
    public sealed class TrainingWorkspaceService
    {
        private static readonly Regex ProjectIdPattern =
            new Regex(@"^Word2019_P\d{2,}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> SupportedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm" };
        private readonly string workingRootPath;

        public TrainingWorkspaceService()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MosWord2019",
                "Working"))
        {
        }

        public TrainingWorkspaceService(string workingRootPath)
        {
            if (string.IsNullOrWhiteSpace(workingRootPath))
                throw new ArgumentException("A Training working root is required.", nameof(workingRootPath));
            this.workingRootPath = Path.GetFullPath(workingRootPath);
        }

        public string WorkingRootPath { get { return workingRootPath; } }

        public string GetWorkingCopyPath(ProjectPackage package)
        {
            PackagePaths paths = GetPackagePaths(package);
            return paths.WorkingPath;
        }

        public string PrepareWorkingCopy(ProjectPackage package)
        {
            PackagePaths paths = GetPackagePaths(package);
            Directory.CreateDirectory(paths.WorkingDirectory);
            if (!File.Exists(paths.WorkingPath))
            {
                File.Copy(paths.StarterPath, paths.WorkingPath, false);
                MakeWritable(paths.WorkingPath);
            }
            return paths.WorkingPath;
        }

        /// <summary>
        /// Replaces only the closed working file. The caller must close Word first.
        /// </summary>
        public string ResetWorkingCopy(ProjectPackage package)
        {
            PackagePaths paths = GetPackagePaths(package);
            Directory.CreateDirectory(paths.WorkingDirectory);
            string temporaryPath = Path.Combine(
                paths.WorkingDirectory,
                ".reset-" + Guid.NewGuid().ToString("N") + paths.Extension);
            try
            {
                File.Copy(paths.StarterPath, temporaryPath, false);
                MakeWritable(temporaryPath);
                if (File.Exists(paths.WorkingPath))
                    File.Replace(temporaryPath, paths.WorkingPath, null, true);
                else
                    File.Move(temporaryPath, paths.WorkingPath);
                MakeWritable(paths.WorkingPath);
                return paths.WorkingPath;
            }
            finally
            {
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
                throw new NotSupportedException("Training working copies support .docx and .docm starters.");

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

        private sealed class PackagePaths
        {
            internal string StarterPath;
            internal string WorkingDirectory;
            internal string WorkingPath;
            internal string Extension;
        }
    }
}
