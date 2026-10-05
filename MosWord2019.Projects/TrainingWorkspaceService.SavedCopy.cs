using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Projects
{
    public sealed partial class TrainingWorkspaceService
    {
        public static bool RequiresExportCheckpoint(ProjectPackage package)
        {
            return package?.Tasks?.Any(t => t.AssertionType == "PlainTextDocumentExport") == true;
        }

        public static bool RequiresLegacyCheckpoint(ProjectPackage package)
        {
            return package?.Tasks?.Any(t => t.AssertionType == "LegacyWordDocumentExport") == true;
        }

        public string GetExportCheckpointPath(ProjectPackage package)
        {
            return Path.Combine(GetPackagePaths(package).WorkingDirectory, "export-checkpoint.docx");
        }

        private void RestoreExportCheckpoint(ProjectPackage package, PackagePaths paths)
        {
            if (!RequiresExportCheckpoint(package) && !RequiresLegacyCheckpoint(package)) return;
            string checkpoint = GetExportCheckpointPath(package);
            if (!File.Exists(checkpoint)) return;
            if ((File.GetAttributes(checkpoint) & FileAttributes.ReparsePoint) != 0) throw new IOException("Checkpoint cannot be a reparse point.");
            // A newer working file may contain valid edits made after a prior trainer session.
            // Never replace those edits with an older retained export snapshot.
            if (File.Exists(paths.WorkingPath) && File.GetLastWriteTimeUtc(paths.WorkingPath) >= File.GetLastWriteTimeUtc(checkpoint)) return;
            // The caller has closed Word before PrepareWorkingCopy. This copies only a trainer-owned
            // formatted checkpoint; the personal TXT output is never opened, overwritten or deleted.
            PreserveSavedWorkingCopy(package, checkpoint);
        }

        /// <summary>
        /// Checkpoint a saved Save As document into this project's own work.docx. A template's main
        /// content type is changed only in the checkpoint, never in the learner's exported template.
        /// Does not launch Word, create a template, or write any package starter/personal output.
        /// </summary>
        public void PreserveSavedWorkingCopy(ProjectPackage package, string savedDocumentPath)
        {
            PackagePaths paths = GetPackagePaths(package);
            string saved = Path.GetFullPath(savedDocumentPath);
            // Legacy work stays binary until the learner converts it. Never put OOXML into work.doc.
            if (paths.Extension == ".doc" && !string.Equals(saved, paths.WorkingPath, StringComparison.OrdinalIgnoreCase))
                paths.WorkingPath = Path.ChangeExtension(paths.WorkingPath, ".docx");
            if (string.Equals(saved, paths.WorkingPath, StringComparison.OrdinalIgnoreCase)) return;
            string extension = Path.GetExtension(saved);
            if (!string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".dotx", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Save As persistence supports nonmacro .docx and .dotx documents only. Keep Word open to recover your work.");
            using (var source = new MemoryStream())
            {
                using (var file = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) file.CopyTo(source);
                source.Position = 0;
                using (var zip = new ZipArchive(source, ZipArchiveMode.Read, true))
                {
                    XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
                    var typeEntry = zip.GetEntry("[Content_Types].xml");
                    if (typeEntry == null || zip.GetEntry("word/document.xml") == null) throw new InvalidDataException("Invalid saved Word package.");
                    XDocument types;
                    using (var stream = typeEntry.Open()) types = XDocument.Load(stream);
                    var main = types.Root.Elements(ct + "Override").SingleOrDefault(e => (string)e.Attribute("PartName") == "/word/document.xml");
                    string type = (string)main?.Attribute("ContentType");
                    if (type != "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" &&
                        type != "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml")
                        throw new InvalidDataException("The saved file is not a supported nonmacro Word document/template.");
                    if (zip.Entries.Any(e => e.FullName.IndexOf("vba", StringComparison.OrdinalIgnoreCase) >= 0) ||
                        types.Root.Elements().Any(e => ((string)e.Attribute("ContentType") ?? "").IndexOf("macro", StringComparison.OrdinalIgnoreCase) >= 0))
                        throw new InvalidDataException("Macro packages cannot be copied into the Training working document.");
                    main.SetAttributeValue("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
                    string temporary = Path.Combine(paths.WorkingDirectory, ".saveas-" + Guid.NewGuid().ToString("N") + ".docx");
                    try
                    {
                        using (var output = File.Create(temporary))
                        using (var target = new ZipArchive(output, ZipArchiveMode.Create))
                            foreach (var entry in zip.Entries)
                                using (var destination = target.CreateEntry(entry.FullName).Open())
                                {
                                    if (entry.FullName == "[Content_Types].xml") types.Save(destination);
                                    else using (var input = entry.Open()) input.CopyTo(destination);
                                }
                        if (File.Exists(paths.WorkingPath)) File.Replace(temporary, paths.WorkingPath, null);
                        else File.Move(temporary, paths.WorkingPath);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
        }
    }
}
