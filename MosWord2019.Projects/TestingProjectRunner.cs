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
    /// <summary>Per-project pipeline, called by the future host on the controller's owning STA.</summary>
    public sealed class TestingProjectRunner : IDisposable
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
        private bool savedAs, disposed, reopenedWorking;
        private string observedExport;
        private int? observedFormat;
        public event Action DocumentReopened;

        public TestingProjectRunner(TestingWorkspaceService workspace, IWordController word, PreparedTestingSession prepared,
            TestingSessionStateStore store, WordGradingService grading, Action<string> log)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Testing Word operations require an owning STA.");
            this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            this.word = word ?? throw new ArgumentNullException(nameof(word));
            this.prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.grading = grading ?? throw new ArgumentNullException(nameof(grading));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            state = word as IWordDocumentState ?? throw new NotSupportedException("Owned document state is required.");
            checkpoint = word as IWordExportCheckpoint ?? throw new NotSupportedException("WordOpenXML checkpoints are required.");
            events = word as IWordSaveEvents ?? throw new NotSupportedException("Owned save events are required.");
            registration = new TestingWordProcessRegistration(word as IWordProcessIdentity, prepared, store);
            events.OwnedDocumentSaving += ObserveSaving;
            events.WorkingFileWritten += WorkingWritten;
        }
        public void Enter(string projectId)
        {
            EnsureOwner();
            if (word.IsOpened) throw new InvalidOperationException("Close the preceding owned document first.");
            workspace.Enter(projectId); savedAs = false; reopenedWorking = false; observedExport = null; observedFormat = null;
            try
            {
                var attempt = workspace.CurrentAttempt;
                store.RecordProjectContext(prepared, projectId, attempt.WorkingPath, new TrainingGradeContext
                { DefaultTemplateFolder = attempt.Outputs.TemplateFolder, OutputOwnership = attempt.Outputs.Capture() }, null);
                OpenWorking();
            }
            catch
            {
                try { if (word.IsOpened) word.CloseDocument(); workspace.Leave(true); }
                catch (Exception ex) { log("Testing entry cleanup deferred: " + ex.Message); }
                throw;
            }
        }
        private void OpenWorking()
        {
            string path = GradePath();
            word.StartWord(); word.OpenDocument(path);
            checkpoint.ConfigureExportCheckpoint(workspace.Files.GetExportCheckpointPath(workspace.CurrentAttempt.Package));
        }
        public void ReopenIfClosed()
        {
            EnsureOwner();
            if (word.IsOpened) return;
            var attempt = workspace.CurrentAttempt ?? throw new InvalidOperationException("No active Testing attempt.");
            string saved = workspace.Files.GetExportCheckpointPath(attempt.Package);
            // Keep untouched binary starters binary; opening them must never answer a conversion task.
            if (File.Exists(saved) && (Path.GetExtension(attempt.WorkingPath) != ".doc" || Path.GetExtension(GradePath()) == ".docx" || string.Equals(Path.GetExtension(observedExport), ".docx", StringComparison.OrdinalIgnoreCase)))
                workspace.Files.PreserveSavedWorkingCopy(attempt.Package, saved);
            OpenWorking(); reopenedWorking = true; DocumentReopened?.Invoke();
        }
        public void ObserveAfterSave()
        {
            EnsureOwner();
            if (!word.IsOpened || workspace.CurrentAttempt == null) return;
            string path = state.CurrentDocumentPath;
            if (!Same(path, GradePath()))
            { savedAs = true; observedExport = path; observedFormat = (word as IWordSaveFormat)?.CurrentSaveFormat; reopenedWorking = false; }
            workspace.CurrentAttempt.Outputs.ObserveOwnedDocument(path);
        }
        private void ObserveSaving(string path) { workspace.CurrentAttempt?.Outputs.ObserveOwnedDocument(path); }
        private void WorkingWritten(string path) { workspace.Files.RecordOwnedWorkingFile(path); }
        public string Autosave()
        {
            EnsureOwner(); ReopenIfClosed(); ObserveAfterSave();
            string live = state.CurrentDocumentPath;
            if (Same(live, GradePath()))
            {
                word.Save(); workspace.Files.RecordOwnedWorkingFile(live);
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
        public IReadOnlyList<ExamTaskGrade> LeaveAndGrade()
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
            word.CloseDocument(); // No COM is used by Core grading; exports still exist until after grading.
            var results = new List<ExamTaskGrade>();
            foreach (var task in tasks)
            {
                string path = source;
                if (task.AssertionType == "SavedWordTemplate")
                {
                    string output = TemplateOutputLocation.ResolveInFolder((string)task.Extra["expectedFileName"], context.DefaultTemplateFolder);
                    if (File.Exists(output)) path = output;
                }
                var result = grading.CheckTask(path, task, context);
                if (result.Outcome == TaskGradeOutcome.Error) log(task.ProjectId + "/" + task.TaskId + ": " + result.Message);
                results.Add(new ExamTaskGrade(attempt.Package.Meta.ProjectId, task.TaskId, result));
            }
            workspace.Leave(true);
            return results.AsReadOnly();
        }
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
            EnsureOwner(); registration.Dispose();
            events.OwnedDocumentSaving -= ObserveSaving; events.WorkingFileWritten -= WorkingWritten;
            disposed = true;
        }
    }
}
