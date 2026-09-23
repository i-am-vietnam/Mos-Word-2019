using MosWord2019.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Office = Microsoft.Office.Core;
using Wd = Microsoft.Office.Interop.Word;

namespace MosWord2019.Word
{
    /// <summary>Owns one Word instance and at most one editable working document.</summary>
    public sealed class WordController : IWordController
    {
        private readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        private WordSession session;
        private bool disposed;

        // Diagnostics only; callers never receive a COM object or a process handle.
        public int? OwnedProcessId { get { return session?.Process?.Id; } }

        public bool IsOpened
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

        public void StartWord()
        {
            EnsureUsable();
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
                session.Application.Caption = caption;
                session.Application.DisplayAlerts = Wd.WdAlertLevel.wdAlertsNone;
                session.Application.AutomationSecurity = Office.MsoAutomationSecurity.msoAutomationSecurityForceDisable;
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

        public void OpenDocument(string filePath)
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

        public void Save()
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

        public void CloseDocument()
        {
            EnsureThread();
            if (session?.Document == null) return;
            try { session.Document.Close(Wd.WdSaveOptions.wdDoNotSaveChanges); }
            catch (COMException ex) when (IsDisconnected(ex)) { }
            // Unexpected failures preserve the reference for retry or full Close cleanup.
            catch (COMException ex) { throw new InvalidOperationException("Could not close the owned Word document.", ex); }
            ReleaseCom(session.Document);
            session.Document = null;
            session.DocumentPath = null;
        }

        public void Close()
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
            catch (Exception ex)
            {
                // If we cannot establish that remaining documents belong to us, do not discard them.
                preserveApplication = true;
                errors.Add(ex);
            }
            finally { TryRelease(documents, errors); }

            try { CloseDocument(); }
            catch (Exception ex) { errors.Add(ex); }
            finally
            {
                TryRelease(session.Document, errors);
                session.Document = null;
                session.DocumentPath = null;
            }
            if (session.Application != null)
            {
                if (!preserveApplication)
                {
                    try { session.Application.Quit(Wd.WdSaveOptions.wdDoNotSaveChanges); }
                    catch (COMException ex) when (IsDisconnected(ex)) { }
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
            Close();
            disposed = true;
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
            if (!string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only existing .docx working copies are supported.", nameof(filePath));
            if (string.Equals(Path.GetFileName(path), "starter.docx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Never open starter.docx for editing. Supply a separate working copy.", nameof(filePath));
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
