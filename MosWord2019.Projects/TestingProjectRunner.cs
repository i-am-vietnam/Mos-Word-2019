using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;

namespace MosWord2019.Projects
{
    /// <summary>Per-project pipeline, called by the Testing host on the controller's owning STA.</summary>
    public sealed class TestingProjectRunner : ITestingProjectRuntime, ITestingForceRecovery, IWordWindowLayout
    {
        private readonly int owner = Thread.CurrentThread.ManagedThreadId;
        private readonly TestingWorkspaceService workspace;
        private readonly IWordController word;
        private readonly IWordDocumentState state;
        private readonly IWordExportCheckpoint checkpoint;
        private readonly IWordSaveEvents events;
        private readonly TestingWordProcessRegistration registration;
        private readonly PreparedTestingSession prepared;
        private readonly TestingSessionStateStore store;
        private readonly WordGradingService grading;
        private readonly Action<string> log;
        private readonly Func<IDisposable> observationBudget;
        private readonly Action forceClose;
        private bool savedAs, disposed, reopenedWorking, deliberateClose;
        private bool inputEnabled = true, closeCheckpointPending, engineSaving, nativeSavePending;
        private readonly IWordCloseEvents closeEvents;
        private string observedExport;
        private int? observedFormat;
        public event Action DocumentReopened;

        public TestingProjectRunner(TestingWorkspaceService workspace, IWordController word, PreparedTestingSession prepared,
            TestingSessionStateStore store, WordGradingService grading, Action<string> log, Func<IDisposable> observationBudget = null, Action forceClose = null)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Testing Word operations require an owning STA.");
            this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            this.word = word ?? throw new ArgumentNullException(nameof(word));
            this.prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.grading = grading ?? throw new ArgumentNullException(nameof(grading));
            this.log = log ?? throw new ArgumentNullException(nameof(log)); this.observationBudget = observationBudget; this.forceClose = forceClose;
            state = word as IWordDocumentState ?? throw new NotSupportedException("Owned document state is required.");
            checkpoint = word as IWordExportCheckpoint ?? throw new NotSupportedException("WordOpenXML checkpoints are required.");
            events = word as IWordSaveEvents ?? throw new NotSupportedException("Owned save events are required.");
            registration = new TestingWordProcessRegistration(word as IWordProcessIdentity, prepared, store);
            events.OwnedDocumentSaving += ObserveSaving;
            events.WorkingFileWritten += WorkingWritten;
            closeEvents = word as IWordCloseEvents;
            if (closeEvents != null) closeEvents.OwnedDocumentClosing += Closing;
        }
        public void Enter(string projectId, bool resume = false)
        {
            EnsureOwner();
            if (word.IsOpened) throw new InvalidOperationException("Close the preceding owned document first.");
            var prior = resume ? store.ReadProjectContext(projectId) : null;
            if (prior == null) workspace.Enter(projectId); else workspace.ResumeAttempt(projectId, prior);
            savedAs = prior?.SavedFromCurrentWorkingDocument ?? false; reopenedWorking = prior != null; closeCheckpointPending = false; nativeSavePending = false;
            observedExport = prior?.OwnedDocumentPath; observedFormat = prior?.OwnedDocumentSaveFormat;
            try
            {
                var attempt = workspace.CurrentAttempt;
                store.RecordProjectContext(prepared, projectId, attempt.WorkingPath, prior ?? new TrainingGradeContext
                { DefaultTemplateFolder = attempt.Outputs.TemplateFolder, OutputOwnership = attempt.Outputs.Capture() }, null);
                if (prior != null)
                {
                    string saved = workspace.Files.GetExportCheckpointPath(attempt.Package);
                    if (File.Exists(saved)) workspace.Files.PreserveSavedWorkingCopy(attempt.Package, saved);
                }
                OpenWorking();
            }
            catch
            {
                try { if (word.IsOpened) CloseDocument(); workspace.Leave(true); }
                catch (Exception ex) { log("Testing entry cleanup deferred: " + ex.Message); }
                throw;
            }
        }
        private void OpenWorking()
        {
            string path = GradePath();
            word.StartWord(); word.OpenDocument(path);
            checkpoint.ConfigureExportCheckpoint(workspace.Files.GetExportCheckpointPath(workspace.CurrentAttempt.Package));
            (word as IWordInputLock)?.SetInputEnabled(inputEnabled);
        }
        public void ReopenIfClosed()
        {
            EnsureOwner();
            if (word.IsOpened) return;
            var attempt = workspace.CurrentAttempt ?? throw new InvalidOperationException("No active Testing attempt.");
            string saved = workspace.Files.GetExportCheckpointPath(attempt.Package);
            // Keep untouched binary starters binary; opening them must never answer a conversion task.
            if (File.Exists(saved) && (closeCheckpointPending || Path.GetExtension(attempt.WorkingPath) != ".doc" || Path.GetExtension(GradePath()) == ".docx" || string.Equals(Path.GetExtension(observedExport), ".docx", StringComparison.OrdinalIgnoreCase)))
                workspace.Files.PreserveSavedWorkingCopy(attempt.Package, saved);
            OpenWorking(); closeCheckpointPending = false; reopenedWorking = true; DocumentReopened?.Invoke();
        }
        public void ObserveAfterSave()
        {
            EnsureOwner();
            if (!word.IsOpened || workspace.CurrentAttempt == null) return;
            string path = state.CurrentDocumentPath;
            if (!Same(path, GradePath()) || nativeSavePending)
            { savedAs |= !Same(path, GradePath()); observedExport = path; observedFormat = (word as IWordSaveFormat)?.CurrentSaveFormat; reopenedWorking = false; nativeSavePending = false;
                store.RecordProjectContext(prepared, workspace.CurrentAttempt.Package.Meta.ProjectId, GradePath(), CaptureContext(), DateTime.UtcNow); }
            workspace.CurrentAttempt.Outputs.ObserveOwnedDocument(path);
        }
        private void Closing(string livePath, int format, string saved)
        {
            if (deliberateClose || workspace.CurrentAttempt == null) return;
            closeCheckpointPending = true;
            if (!Same(saved, workspace.Files.GetExportCheckpointPath(workspace.CurrentAttempt.Package))) throw new InvalidDataException("Unexpected close checkpoint.");
            if (reopenedWorking && observedExport != null && Same(livePath, GradePath()))
            { livePath = observedExport; format = observedFormat ?? format; }
            savedAs |= !Same(livePath, GradePath()); observedExport = livePath; observedFormat = format;
            workspace.CurrentAttempt.Outputs.ObserveOwnedDocument(livePath);
            workspace.Files.RecordOwnedWorkingFile(saved);
            // The native document may deny delete sharing: promote only after it has closed.
            var attempt = workspace.CurrentAttempt;
            store.RecordProjectContext(prepared, attempt.Package.Meta.ProjectId, saved, new TrainingGradeContext
            { OwnedDocumentPath = livePath, OwnedDocumentSaveFormat = format, SavedFromCurrentWorkingDocument = savedAs,
              DefaultTemplateFolder = attempt.Outputs.TemplateFolder, OutputOwnership = attempt.Outputs.Capture() }, DateTime.UtcNow);
        }
        private void CloseDocument()
        { deliberateClose = true; try { word.CloseDocument(); } finally { deliberateClose = false; } }
        private void ObserveSaving(string path) { if (!engineSaving) nativeSavePending = true; workspace.CurrentAttempt?.Outputs.ObserveOwnedDocument(path); }
        private void WorkingWritten(string path) { workspace.Files.RecordOwnedWorkingFile(path); }
        public string Autosave()
        {
            EnsureOwner(); ReopenIfClosed(); ObserveAfterSave();
            string live = state.CurrentDocumentPath;
            if (Same(live, GradePath()))
            {
                engineSaving = true; try { word.Save(); } finally { engineSaving = false; }
                workspace.Files.RecordOwnedWorkingFile(live);
            }
            else
            {
                // Never Save a TXT/template/arbitrary learner path. Export the live formatted document instead.
                string path = checkpoint.SaveFormattedCheckpoint();
                if (!Same(path, workspace.Files.GetExportCheckpointPath(workspace.CurrentAttempt.Package))) throw new InvalidDataException("Unexpected checkpoint destination.");
                workspace.Files.RecordOwnedWorkingFile(path);
                workspace.Files.PreserveSavedWorkingCopy(workspace.CurrentAttempt.Package, path);
            }
            var attempt = workspace.CurrentAttempt;
            store.RecordProjectContext(prepared, attempt.Package.Meta.ProjectId, GradePath(), CaptureContext(), DateTime.UtcNow);
            return GradePath();
        }
        private TrainingGradeContext CaptureContext() => new TrainingGradeContext
        {
            OwnedDocumentPath = reopenedWorking && observedExport != null ? observedExport : state.CurrentDocumentPath,
            OwnedDocumentSaveFormat = reopenedWorking && observedExport != null ? observedFormat : (word as IWordSaveFormat)?.CurrentSaveFormat,
            DefaultTemplateFolder = state.DefaultTemplateFolder, SavedFromCurrentWorkingDocument = savedAs,
            OutputOwnership = workspace.CurrentAttempt.Outputs.Capture(state.CurrentDocumentPath)
        };
        public IReadOnlyList<ExamTaskGrade> LeaveAndGrade(Action<IReadOnlyList<ExamTaskGrade>> persist = null)
        {
            using (observationBudget?.Invoke()) return LeaveAndGradeCore(persist);
        }
        private IReadOnlyList<ExamTaskGrade> LeaveAndGradeCore(Action<IReadOnlyList<ExamTaskGrade>> persist)
        {
            EnsureOwner(); string source = Autosave(); var attempt = workspace.CurrentAttempt;
            var selected = prepared.Definition.Projects.Single(p => p.ProjectId == attempt.Package.Meta.ProjectId);
            var tasks = selected.TaskIds.Select(id => attempt.Package.Tasks.Single(t => t.TaskId == id)).ToArray();
            var context = CaptureContext();
            if (tasks.Any(t => t.AssertionType == WordFormattingMarksSnapshot.AssertionType))
                context.FormattingMarks = (word as IWordViewState)?.ReadFormattingMarks();
            if (tasks.Any(WordGradingService.RequiresTocLayout))
            {
                var live = (word as IWordTocLayout)?.ReadTocLayout();
                if (live != null) context.TocLayout = new WordTocLayoutSnapshot(source, live.Targets);
            }
            store.RecordProjectContext(prepared, attempt.Package.Meta.ProjectId, source, context, DateTime.UtcNow);
            CloseDocument(); // No COM is used by Core grading; exports still exist until after grading.
            var results = new List<ExamTaskGrade>();
            foreach (var task in tasks)
            {
                string path = source;
                if (task.AssertionType == "SavedWordTemplate")
                {
                    string output = TemplateOutputLocation.ResolveInFolder((string)task.Extra["expectedFileName"], context.DefaultTemplateFolder);
                    if (File.Exists(output)) path = output;
                }
                if (task.AssertionType == "ModernWordDocumentFormat" && Path.GetExtension(attempt.Package.Meta.Starter) == ".doc" &&
                    string.Equals(Path.GetExtension(context.OwnedDocumentPath), ".doc", StringComparison.OrdinalIgnoreCase)) path = attempt.WorkingPath.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)
                        ? attempt.WorkingPath : Path.ChangeExtension(attempt.WorkingPath, ".doc");
                var result = grading.CheckTask(path, task, context);
                if (result.Outcome == TaskGradeOutcome.Error) log(task.ProjectId + "/" + task.TaskId + ": " + result.Message);
                results.Add(new ExamTaskGrade(attempt.Package.Meta.ProjectId, task.TaskId, result));
            }
            persist?.Invoke(results.AsReadOnly());
            workspace.Leave(true);
            return results.AsReadOnly();
        }
        void ITestingProjectRuntime.LeaveAndGrade(Action<IReadOnlyList<ExamTaskGrade>> persist) { LeaveAndGrade(persist); }
        public void AutosaveCurrent() { Autosave(); }
        public void Observe() { using (observationBudget?.Invoke()) { ReopenIfClosed(); ObserveAfterSave(); } }
        public void SetInputEnabled(bool enabled) { inputEnabled = enabled; (word as IWordInputLock)?.SetInputEnabled(enabled); }
        public void Restart(string projectId) { EnsureOwner(); CloseDocument(); workspace.Leave(true); Enter(projectId); }
        public IReadOnlyList<ExamTaskGrade> GradeSaved(string projectId)
        {
            EnsureOwner(); var package = workspace.Package(projectId); var selected = prepared.Definition.Projects.Single(p => p.ProjectId == projectId);
            var context = store.ReadProjectContext(projectId); string source = workspace.SavedPath(projectId);
            bool visited = context != null;
            if (!visited) source = Path.Combine(package.ProjectFolderPath, package.Meta.Starter);
            else
            {
                string checkpointPath = workspace.Files.GetExportCheckpointPath(package);
                if (File.Exists(checkpointPath)) source = checkpointPath; // Read recovery input directly; Word may still hold the working file.
                var ledger = OutputOwnershipLedger.Restore(package, context.OutputOwnership, context.OutputOwnership.DocumentsFolder,
                    context.DefaultTemplateFolder, prepared.SessionId, workspace.Journal, log);
                context.OutputOwnership = ledger.Capture();
            }
            var results = new List<ExamTaskGrade>();
            foreach (var task in package.Tasks.Where(t => selected.TaskIds.Contains(t.TaskId)))
            {
                string path = source;
                if (task.AssertionType == "SavedWordTemplate" && context != null)
                { var output = TemplateOutputLocation.ResolveInFolder((string)task.Extra["expectedFileName"], context.DefaultTemplateFolder); if (File.Exists(output)) path = output; }
                if (task.AssertionType == "ModernWordDocumentFormat" && Path.GetExtension(package.Meta.Starter) == ".doc" &&
                    (context == null || string.Equals(Path.GetExtension(context.OwnedDocumentPath), ".doc", StringComparison.OrdinalIgnoreCase)))
                    path = Path.Combine(package.ProjectFolderPath, package.Meta.Starter);
                var result = grading.CheckTask(path, task, context);
                if (result.Outcome == TaskGradeOutcome.Error) log(projectId + "/" + task.TaskId + ": " + result.Message);
                results.Add(new ExamTaskGrade(projectId, task.TaskId, result));
            }
            return results.AsReadOnly();
        }
        public void ForceCloseOwnedWord()
        {
            EnsureOwner(); if (forceClose == null) throw new InvalidOperationException("Verified Word termination is unavailable.");
            forceClose();
            deliberateClose = true; try { word.Close(); } finally { deliberateClose = false; }
        }
        public void SetWindowBounds(int left, int top, int width, int height)
        { EnsureOwner(); (word as IWordWindowLayout)?.SetWindowBounds(left, top, width, height); }
        public void Close()
        { EnsureOwner(); if (word.IsOpened) CloseDocument(); word.Close(); workspace.CleanupCompleted(true); }
        private string GradePath()
        {
            var attempt = workspace.CurrentAttempt ?? throw new InvalidOperationException("No active Testing attempt.");
            string converted = Path.ChangeExtension(attempt.WorkingPath, ".docx");
            return File.Exists(converted) ? converted : attempt.WorkingPath;
        }
        private static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        private void EnsureOwner()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TestingProjectRunner));
            if (Thread.CurrentThread.ManagedThreadId != owner) throw new InvalidOperationException("Use the owning Testing STA.");
        }
        public void Dispose()
        {
            if (disposed) return;
            EnsureOwner();
            try { if (word.IsOpened) { Autosave(); CloseDocument(); } word.Dispose(); }
            finally
            {
                registration.Dispose();
                if (closeEvents != null) closeEvents.OwnedDocumentClosing -= Closing;
                events.OwnedDocumentSaving -= ObserveSaving; events.WorkingFileWritten -= WorkingWritten;
                disposed = true;
            }
        }
    }
}
