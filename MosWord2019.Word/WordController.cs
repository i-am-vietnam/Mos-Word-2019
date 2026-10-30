using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Office = Microsoft.Office.Core;
using Wd = Microsoft.Office.Interop.Word;

namespace MosWord2019.Word
{
    /// <summary>Owns one Word instance and at most one editable working document.</summary>
    public sealed partial class WordController : IWordController, IWordWindowLayout, IWordDocumentState, IWordSaveFormat, IWordExportCheckpoint, IWordViewState, IWordTocLayout, IWordSaveEvents, IWordProcessIdentity
    {
        private readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        private WordSession session;
        private bool disposed;
        private OleMessageFilter messageFilter;
        private readonly Func<OleMessageFilter> createMessageFilter;

        public WordController() : this(() => new OleMessageFilter()) { }

        internal WordController(Func<OleMessageFilter> createMessageFilter)
        {
            this.createMessageFilter = createMessageFilter ?? throw new ArgumentNullException(nameof(createMessageFilter));
        }

        // Diagnostics only; callers never receive a COM object or a process handle.
        public event Action<MosWord2019.Core.Models.OwnedWordProcessIdentity> OwnedProcessStarted;
        public MosWord2019.Core.Models.OwnedWordProcessIdentity OwnedProcessIdentity
        { get { EnsureUsable(); return session?.Process?.ReadIdentity(); } }
        public int? OwnedProcessId { get { return session?.Process?.Id; } }

        /// <summary>Limits OLE rejection retries inside a short owner-STA observation.</summary>
        public IDisposable BeginShortCallBudget(int milliseconds)
        {
            EnsureUsable();
            if (milliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            return messageFilter != null ? messageFilter.BeginShortCallBudget(milliseconds) : new EmptyCallBudget();
        }

        private sealed class EmptyCallBudget : IDisposable
        {
            public void Dispose() { }
        }

        public int CurrentSaveFormat { get { return RunCom(() => CurrentSaveFormatCore); } }

        private int CurrentSaveFormatCore
        {
            get
            {
                EnsureUsable();
                if (!IsOpened) throw new InvalidOperationException("No owned document is open.");
                return session.Document.SaveFormat;
            }
        }

        public string CurrentDocumentPath { get { return RunCom(() => CurrentDocumentPathCore); } }

        private string CurrentDocumentPathCore
        {
            get
            {
                EnsureUsable();
                if (!IsOpened) throw new InvalidOperationException("No owned document is open.");
                return session.Document.FullName;
            }
        }

        public string DefaultTemplateFolder { get { return RunCom(() => DefaultTemplateFolderCore); } }

        private string DefaultTemplateFolderCore
        {
            get
            {
                EnsureUsable();
                if (!IsOpened) throw new InvalidOperationException("No owned document is open.");
                return MosWord2019.Core.Services.TemplateFolderResolver.Resolve(session.Application.Version);
            }
        }

        public bool IsOpened { get { return RunCom(() => IsOpenedCore); } }

        private bool IsOpenedCore
        {
            get
            {
                EnsureUsable();
                if (session?.Document == null) return false;
                try { return !string.IsNullOrEmpty(session.Document.FullName); }
                catch (COMException ex) when (IsDisconnected(ex)) { return false; }
                catch (COMException ex) { throw new InvalidOperationException("Could not inspect the Word document.", ex); }
            }
        }

        public void StartWord() { RunCom(() => StartWordCore()); }

        private void StartWordCore()
        {
            EnsureUsable();
            if (messageFilter == null) messageFilter = createMessageFilter();
            if (session != null)
            {
                if (session.Application != null)
                {
                    try { string version = session.Application.Version; return; }
                    catch (COMException ex) when (IsDisconnected(ex)) { }
                }
                Close();
            }

            var preexisting = WinApiProcessHelper.SnapshotWordProcessIds();
            DateTime activationUtc = DateTime.UtcNow;
            string caption = null;
            try
            {
                // Never GetActiveObject: activation creates a dedicated automation instance.
                session = new WordSession { Application = new Wd.Application() };
                caption = session.Application.Caption;
                string token = "MosWord2019-" + Guid.NewGuid().ToString("N");
                session.Application.Caption = token;
                session.Application.Visible = true;
                session.Process = WinApiProcessHelper.Capture(token, preexisting, activationUtc);
                OwnedProcessStarted?.Invoke(session.Process.ReadIdentity());
                session.Application.Caption = caption;
                session.Application.DisplayAlerts = Wd.WdAlertLevel.wdAlertsNone;
                session.Application.AutomationSecurity = Office.MsoAutomationSecurity.msoAutomationSecurityForceDisable;
                session.Application.DocumentBeforeSave += BeforeDocumentSave;
            }
            catch (WinApiProcessHelper.OwnershipRejectedException)
            {
                // Never Quit an application if its process was identified as pre-existing.
                try { if (caption != null) session.Application.Caption = caption; }
                finally
                {
                    ReleaseCom(session.Application);
                    session = null;
                }
                throw;
            }
            catch (Exception ex)
            {
                try { Close(); }
                catch (Exception cleanup) { throw new AggregateException("Word startup and cleanup failed.", ex, cleanup); }
                throw new InvalidOperationException("Microsoft Word desktop could not be started. Check its installation and desktop COM registration.", ex);
            }
        }

        public void OpenDocument(string filePath) { RunCom(() => OpenDocumentCore(filePath)); }

        private void OpenDocumentCore(string filePath)
        {
            EnsureUsable();
            if (IsOpened) throw new InvalidOperationException("Close the current Word document before opening another.");
            string path = ValidateWorkingCopy(filePath);
            CloseDocument(); // Release a stale reference after manual document closure.
            StartWord();
            Wd.Documents documents = null;
            try
            {
                documents = session.Application.Documents;
                if (documents.Count != 0)
                    throw new InvalidOperationException("This Word instance already contains an externally opened document.");
                // Nonempty unknown passwords avoid interactive password prompts; encrypted files are unsupported.
                string password = Guid.NewGuid().ToString("N");
                session.Document = documents.Open(path, ConfirmConversions: false, ReadOnly: false,
                    AddToRecentFiles: false, PasswordDocument: password, WritePasswordDocument: password,
                    Visible: true, OpenAndRepair: false, NoEncodingDialog: true);
                session.DocumentPath = path;
                if (session.Document.ReadOnly)
                    throw new IOException("Word opened the working copy read-only. Close other users of this file before retrying.");
            }
            catch (Exception ex)
            {
                // Release the collection before document/application teardown.
                ReleaseCom(documents);
                documents = null;
                try { Close(); }
                catch (Exception cleanup) { throw new AggregateException("Opening the Word document and cleanup failed.", ex, cleanup); }
                throw new InvalidOperationException("Could not open the Word working copy: " + path, ex);
            }
            finally { ReleaseCom(documents); }
        }

        public void SetWindowBounds(int left, int top, int width, int height) { RunCom(() => SetWindowBoundsCore(left, top, width, height)); }

        private void SetWindowBoundsCore(int left, int top, int width, int height)
        {
            EnsureUsable();
            if (!IsOpened || session.Process == null)
                throw new InvalidOperationException("No owned Word document is available for positioning.");
            Wd.Window window = null;
            try
            {
                window = session.Document.ActiveWindow;
                session.Process.PositionWindow(new IntPtr(window.Hwnd), left, top, width, height);
            }
            finally { ReleaseCom(window); }
        }

        public void Save() { RunCom(() => SaveCore()); }

        private void SaveCore()
        {
            EnsureUsable();
            if (!IsOpened) throw new InvalidOperationException("No Word document is open. It may have been closed manually.");
            try
            {
                if (session.Document.ReadOnly) throw new IOException("The Word document is read-only.");
                session.Document.Save();
                if (!session.Document.Saved) throw new IOException("Word did not confirm that the document was saved.");
            }
            catch (Exception ex) when (ex is COMException || ex is IOException)
            {
                throw new InvalidOperationException("Could not save the Word document. The document remains available for recovery where possible.", ex);
            }
        }

        public void CloseDocument() { RunCom(() => CloseDocumentCore()); }

        private void CloseDocumentCore()
        {
            EnsureThread();
            // Switching/reset must never proceed while this application contains a
            // document not owned by this controller. Full Close has its own preservation path.
            Wd.Documents documents = null;
            try
            {
                if (session?.Application != null)
                {
                    documents = session.Application.Documents;
                    int owned = session.Document != null && DocumentStillOpen() ? 1 : 0;
                    if (documents.Count > owned)
                        throw new InvalidOperationException("Close the additional documents in this Word application before ending the Training session.");
                }
            }
            catch (COMException ex) when (IsDisconnected(ex)) { }
            finally { ReleaseCom(documents); }
            CloseOwnedDocument();
        }

        private void CloseOwnedDocument()
        {
            if (session?.Document == null) return;
            try { session.Document.Close(Wd.WdSaveOptions.wdDoNotSaveChanges); }
            catch (COMException ex) when (IsDisconnected(ex)) { }
            // Unexpected failures preserve the reference for retry or full Close cleanup.
            catch (COMException ex) { throw new InvalidOperationException("Could not close the owned Word document.", ex); }
            ReleaseCom(session.Document);
            session.Document = null;
            session.DocumentPath = null;
            exportCheckpointPath = null;
        }

        public void Close() { RunCom(() => CloseCore()); }

        private void CloseCore()
        {
            EnsureThread();
            if (session == null) return;
            var errors = new List<Exception>();
            bool preserveApplication = false;
            Wd.Documents documents = null;
            try
            {
                if (session.Application != null)
                {
                    bool ownedDocumentOpen = session.Document != null && DocumentStillOpen();
                    documents = session.Application.Documents;
                    preserveApplication = documents.Count > (ownedDocumentOpen ? 1 : 0);
                }
            }
            catch (COMException ex) when (IsDisconnected(ex)) { }
            catch (Exception ex) when (IsDeferredCleanup(ex)) { throw; }
            catch (Exception ex)
            {
                // If we cannot establish that remaining documents belong to us, do not discard them.
                preserveApplication = true;
                errors.Add(ex);
            }
            finally { TryRelease(documents, errors); }

            try { CloseOwnedDocument(); }
            catch (Exception ex) when (IsDeferredCleanup(ex)) { throw; }
            catch (Exception ex) { errors.Add(ex); }
            TryRelease(session.Document, errors);
            session.Document = null;
            session.DocumentPath = null;
            if (session.Application != null)
            {
                try { session.Application.DocumentBeforeSave -= BeforeDocumentSave; }
                catch (COMException ex) when (IsDisconnected(ex)) { }
                if (!preserveApplication)
                {
                    try { session.Application.Quit(Wd.WdSaveOptions.wdDoNotSaveChanges); }
                    catch (COMException ex) when (IsDisconnected(ex)) { }
                    // A modal dialog is recoverable; never fall through to process termination.
                    catch (Exception ex) when (IsDeferredCleanup(ex)) { throw; }
                    catch (Exception ex) { errors.Add(ex); }
                }
                TryRelease(session.Application, errors);
                session.Application = null;
            }
            if (preserveApplication)
            {
                // A learner opened another document, or ownership could not be established.
                session.Process?.Dispose();
                session = null;
                errors.Add(new InvalidOperationException("Word was left running to protect documents not confirmed as controller-owned. Close those documents in Word."));
            }
            else
            {
                try
                {
                    session.Process?.EnsureExited();
                    session.Process?.Dispose();
                    session = null;
                }
                catch (Exception ex) { errors.Add(ex); } // Keep the handle for a subsequent Close retry.
            }
            if (errors.Count > 0) throw new AggregateException("Word cleanup encountered an infrastructure error.", errors);
        }

        public void Dispose()
        {
            DisposeOwnedState(Close);
        }

        // A cleanup callback permits lifecycle failure tests without activating Word.
        internal void DisposeOwnedState(Action close)
        {
            EnsureThread();
            if (disposed) return;
            Exception cleanupError = null;
            try
            {
                // A failed previous Dispose restored the STA filter, but kept the live session for Retry.
                if (session != null && messageFilter == null) messageFilter = createMessageFilter();
                close();
            }
            catch (Exception ex) { cleanupError = ex; }
            finally
            {
                // Registration belongs to the STA, even when document/process cleanup fails.
                try
                {
                    if (messageFilter != null)
                    {
                        messageFilter.Dispose();
                        messageFilter = null;
                    }
                }
                catch (Exception restoreError)
                {
                    if (cleanupError != null)
                        throw new AggregateException("Word cleanup and STA filter restoration failed.", cleanupError, restoreError);
                    throw;
                }
            }
            if (cleanupError != null) ExceptionDispatchInfo.Capture(cleanupError).Throw();
            disposed = true;
        }

        private static T RunCom<T>(Func<T> action)
        {
            try { return action(); }
            catch (Exception ex) when (!(ex is WordBusyException) && WordBusyException.IsBusy(ex))
            { throw new WordBusyException(ex); }
        }

        private static void RunCom(Action action)
        {
            RunCom(() => { action(); return true; });
        }

        private static bool IsDeferredCleanup(Exception exception)
        {
            if (WordBusyException.IsBusy(exception)) return true;
            // Word may send a synchronous input callback. Calling out again here is forbidden;
            // preserve the references and let the owner STA retry after that callback returns.
            var pending = new Stack<Exception>();
            var seen = new HashSet<Exception>();
            pending.Push(exception);
            while (pending.Count != 0)
            {
                Exception current = pending.Pop();
                if (current == null || !seen.Add(current)) continue;
                var com = current as COMException;
                if (com != null && com.ErrorCode == unchecked((int)0x8001010D)) return true;
                var aggregate = current as AggregateException;
                if (aggregate != null)
                    foreach (Exception inner in aggregate.InnerExceptions) pending.Push(inner);
                if (current.InnerException != null) pending.Push(current.InnerException);
            }
            return false;
        }

        private bool DocumentStillOpen()
        {
            try { return !string.IsNullOrEmpty(session.Document.FullName); }
            catch (COMException ex) when (IsDisconnected(ex)) { return false; }
        }

        private void EnsureUsable()
        {
            EnsureThread();
            if (disposed) throw new ObjectDisposedException(nameof(WordController));
        }

        private void EnsureThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Use WordController only on its creating STA thread.");
        }

        private static string ValidateWorkingCopy(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A document path is required.", nameof(filePath));
            string path = Path.GetFullPath(filePath);
            if (!string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(path), ".doc", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only existing .docx or legacy .doc working copies are supported.", nameof(filePath));
            if (string.Equals(Path.GetFileNameWithoutExtension(path), "starter", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Never open a starter for editing. Supply a separate working copy.", nameof(filePath));
            if (!File.Exists(path)) throw new FileNotFoundException("The Word working copy does not exist.", path);
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("The working copy is read-only.");
            // Fail before startup on locked files; Word's ReadOnly property also guards an open-time race.
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return path;
        }

        private static bool IsDisconnected(COMException exception)
        {
            uint code = unchecked((uint)exception.ErrorCode);
            // RPC disconnection/server-exit and Word's deleted-document errors.
            return code == 0x80010108 || code == 0x800706BA || code == 0x800706BE || code == 0x80010007 ||
                code == 0x800A16C9 || code == 0x800A01A8;
        }

        private static void ReleaseCom(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }

        private static void TryRelease(object value, List<Exception> errors)
        {
            try { ReleaseCom(value); }
            catch (Exception ex) { errors.Add(ex); }
        }
    }
}
