using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Projects
{
    /// <summary>Testing paths and attempt ownership. No Word calls, timer, or arbitrary-folder scanning.</summary>
    public sealed class TestingWorkspaceService
    {
        private readonly SessionRecoveryJournal journal;
        private readonly string documents, templates, pictures;
        private readonly Action<string> log;
        private readonly Dictionary<string, ProjectPackage> projects = new Dictionary<string, ProjectPackage>(StringComparer.Ordinal);
        private PreparedTestingSession preparation;
        public string SessionId { get; }
        public string RootPath { get; }
        public TrainingWorkspaceService Files { get; }
        public TestingProjectAttempt CurrentAttempt { get; private set; }

        public TestingWorkspaceService(string sessionId, SessionRecoveryJournal journal, string documents = null,
            string pictures = null, string templates = null, Action<string> log = null)
        {
            Guid id;
            if (!Guid.TryParseExact(sessionId, "N", out id)) throw new ArgumentException("A session GUID is required.");
            this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
            SessionId = sessionId;
            this.documents = Path.GetFullPath(documents ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            this.pictures = Path.GetFullPath(pictures ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            this.templates = Path.GetFullPath(templates ?? MosWord2019.Core.Services.TemplateFolderResolver.Resolve());
            this.log = log ?? (s => MosWord2019.Core.Diagnostics.AppLogger.Write(s));
            RootPath = Path.Combine(this.documents, "MosWord2019", "Working", "Testing", sessionId);
            RejectReparse(RootPath);
            if (Directory.Exists(RootPath) || File.Exists(RootPath)) throw new IOException("A fresh Testing workspace must not already exist.");
            Files = new TrainingWorkspaceService(RootPath, this.documents, this.pictures, journal, SessionId);
        }

        public void Preflight(PreparedTestingSession prepared, IEnumerable<ProjectPackage> packages)
        {
            if (CurrentAttempt != null) throw new InvalidOperationException("Preflight must precede project entry.");
            preparation = null; projects.Clear(); // Failed preflight cannot leave an earlier authorization active.
            if (prepared == null || prepared.SessionId != SessionId) throw new InvalidDataException("Testing preparation/session mismatch.");
            var available = packages.ToDictionary(p => p.Meta.ProjectId, StringComparer.Ordinal);
            var checkedProjects = new Dictionary<string, ProjectPackage>(StringComparer.Ordinal);
            prepared.VerifyCatalog();
            foreach (var selected in prepared.Definition.Projects)
            {
                ProjectPackage source;
                if (!available.TryGetValue(selected.ProjectId, out source)) throw new InvalidDataException("Selected package is unavailable.");
                var package = new ProjectLoader().Load(source.ProjectFolderPath, prepared.Language);
                if (package == null) throw new InvalidDataException("Selected package failed validation: " + selected.ProjectId);
                prepared.VerifyPackage(package);
                if (selected.ProjectId != package.Meta.ProjectId || !package.Tasks.Where(t => selected.TaskIds.Contains(t.TaskId)).Select(t => t.TaskId).SequenceEqual(selected.TaskIds) ||
                    selected.TaskIds.Any(id => package.Tasks.Single(t => t.TaskId == id).InstructionKey != selected.InstructionKeys[id]))
                    throw new InvalidDataException("Selected tasks no longer match the audited package.");
                CheckForeignFiles(package);
                checkedProjects.Add(selected.ProjectId, package);
            }
            projects.Clear(); foreach (var pair in checkedProjects) projects.Add(pair.Key, pair.Value);
            preparation = prepared;
        }

        private void CheckForeignFiles(ProjectPackage package)
        {
            TrainingWorkspaceService.ValidateAssetOptions(package);
            string assets = Path.Combine(package.ProjectFolderPath, "assets");
            RejectReparse(assets);
            var copies = new List<Tuple<string, string>>();
            if (Directory.Exists(assets) && (package.Meta.AssetStaging == null || package.Meta.AssetStaging.CopyAssetsToDocuments))
                copies.AddRange(Directory.GetFiles(assets, "*", SearchOption.TopDirectoryOnly).Select(p => Tuple.Create(p, Path.Combine(documents, Path.GetFileName(p)))));
            if (package.Meta.AssetStaging != null)
                copies.AddRange(package.Meta.AssetStaging.AdditionalCopies.Select(c => Tuple.Create(Path.Combine(assets, c.Source),
                    Path.Combine(c.Destination == "Pictures" ? pictures : documents, c.DestinationFileName ?? c.Source))));
            foreach (var copy in copies)
            {
                RejectReparse(copy.Item1); RejectReparse(copy.Item2);
                if (Directory.Exists(copy.Item2) || (File.Exists(copy.Item2) && PreparedTestingSession.Hash(copy.Item1) != PreparedTestingSession.Hash(copy.Item2)))
                    throw new IOException("Testing asset conflicts with a foreign file: " + copy.Item2);
            }
            // Pre-existing declared outputs are allowed: entry-time freshness baselines protect them.

        }

        public TestingProjectAttempt Enter(string projectId)
        {
            if (CurrentAttempt != null) throw new InvalidOperationException("Close the preceding project first.");
            ProjectPackage package;
            if (preparation == null || !projects.TryGetValue(projectId, out package)) throw new InvalidOperationException("Preflight is required.");
            preparation.VerifyCatalog(); preparation.VerifyPackage(package);
            CheckForeignFiles(package); // Recheck races before staging; Phase A still uses CreateNew/no overwrite.
            var outputs = new OutputOwnershipLedger(package, documents, templates, log, SessionId, journal);
            string path = Files.PrepareFreshWorkingCopy(package);
            try { Files.StageProjectAssetsToDocuments(package); }
            catch { Files.CleanupProjectAssets(package, log); throw; }
            return CurrentAttempt = new TestingProjectAttempt(package, path, outputs);
        }

        public void Leave(bool ownedDocumentClosed)
        {
            if (!ownedDocumentClosed) throw new InvalidOperationException("Close owned Word before Testing cleanup.");
            if (CurrentAttempt == null) return;
            CurrentAttempt.Outputs.Cleanup(true);
            Files.CleanupProjectAssets(CurrentAttempt.Package, log);
            CurrentAttempt = null; // Keep reserved working files/journal for later session recovery.
        }
        public TestingProjectAttempt Restart(bool ownedDocumentClosed)
        {
            if (CurrentAttempt == null) throw new InvalidOperationException("No active Testing project.");
            string id = CurrentAttempt.Package.Meta.ProjectId;
            Leave(ownedDocumentClosed);
            return Enter(id); // Fresh baseline precedes the next owned document open.
        }
        public void CleanupCompleted(bool ownedDocumentClosed)
        {
            if (!ownedDocumentClosed) throw new InvalidOperationException("Close owned Word before Testing cleanup.");
            Leave(true); Files.CleanupWorkingFiles(log);
        }
        private static void RejectReparse(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Testing paths must not traverse reparse points: " + current);
        }
    }

    public sealed class TestingProjectAttempt
    {
        public ProjectPackage Package { get; }
        public string WorkingPath { get; }
        public OutputOwnershipLedger Outputs { get; }
        internal TestingProjectAttempt(ProjectPackage package, string workingPath, OutputOwnershipLedger outputs)
        { Package = package; WorkingPath = workingPath; Outputs = outputs; }
    }
}
