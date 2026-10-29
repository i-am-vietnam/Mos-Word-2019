using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;

namespace MosWord2019.Projects
{
    /// <summary>Declared-output sessions. Cleanup requires the caller's successful owned-document close.</summary>
    public sealed class TemplateOutputCleanupService
    {
        private readonly string documentsFolder;
        private readonly Func<string> templateFolder;
        private readonly Action<string> log;
        private readonly List<OutputOwnershipLedger> ledgers = new List<OutputOwnershipLedger>();

        public TemplateOutputCleanupService()
            : this(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) { }

        public TemplateOutputCleanupService(string documentsFolder, Func<string> templateFolder = null, Action<string> log = null)
        {
            if (string.IsNullOrWhiteSpace(documentsFolder) || !Path.IsPathRooted(documentsFolder))
                throw new ArgumentException("An absolute Documents folder is required.", nameof(documentsFolder));
            this.documentsFolder = Path.GetFullPath(documentsFolder);
            this.templateFolder = templateFolder ?? (() => TemplateFolderResolver.Resolve());
            this.log = log ?? (message => AppLogger.Write(message));
        }

        public OutputOwnershipLedger CurrentLedger { get; private set; }

        // Call after closing/cleaning the previous attempt, and before opening Word on Go or Restart.
        public void Register(ProjectPackage package)
        {
            if (CurrentLedger != null) throw new InvalidOperationException("Close the preceding output session before registering another.");
            bool templates = package.Tasks.Any(t => t.AssertionType == TemplateOutputLocation.AssertionType || t.AssertionType == "SavedWordTemplate");
            var ledger = new OutputOwnershipLedger(package, documentsFolder, templates ? templateFolder() : null, log);
            ledgers.Add(ledger);
            CurrentLedger = ledger;
        }

        public void ObserveOwnedDocument(string path) { CurrentLedger?.ObserveOwnedDocument(path); }
        public OutputOwnershipSnapshot Capture(string ownedDocumentPath = null) => CurrentLedger?.Capture(ownedDocumentPath);

        // The legacy parameterless call cannot prove that Word released its document.
        public void Cleanup() { Cleanup(false); }

        public void Cleanup(bool ownedDocumentClosed)
        {
            foreach (var ledger in ledgers) ledger.Cleanup(ownedDocumentClosed);
            if (ownedDocumentClosed)
            {
                ledgers.Clear();
                CurrentLedger = null;
            }
        }
    }
}
