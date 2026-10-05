using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using MosWord2019.Core.Diagnostics;
using Wd = Microsoft.Office.Interop.Word;

namespace MosWord2019.Word
{
    public sealed partial class WordController
    {
        private string exportCheckpointPath;
        private volatile bool writingCheckpoint;
        private bool legacyCheckpoint;
        private string legacyCheckpointError;
        public event Action<string> ExportCheckpointFailed;

        public void ConfigureLegacyCheckpoint(string checkpointPath)
        {
            legacyCheckpoint = true;
            legacyCheckpointError = null;
            ConfigureExportCheckpoint(checkpointPath);
        }

        public string GetPreservedFormattedPath()
        {
            EnsureUsable();
            if (!legacyCheckpoint || !IsOpened || exportCheckpointPath == null)
                throw new InvalidOperationException("No legacy-output preservation is configured.");
            if (session.Document.SaveFormat != (int)Wd.WdSaveFormat.wdFormatDocument)
                SaveFormattedCheckpoint();
            if (legacyCheckpointError != null || !File.Exists(exportCheckpointPath))
                throw new IOException("The formatted state preceding legacy Save As could not be preserved. " + legacyCheckpointError);
            return exportCheckpointPath;
        }

        public void ConfigureExportCheckpoint(string checkpointPath)
        {
            EnsureUsable();
            if (!IsOpened) throw new InvalidOperationException("No owned document is open.");
            string path = Path.GetFullPath(checkpointPath);
            if (!string.Equals(Path.GetFileName(path), "export-checkpoint.docx", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(session.Document.FullName), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An export checkpoint must be the exact Training checkpoint beside the owned working document.");
            exportCheckpointPath = path;
            SaveFormattedCheckpoint(); // Establish a valid checkpoint before native learner operations.
        }

        private void BeforeDocumentSave(Wd.Document document, ref bool saveAsUi, ref bool cancel)
        {
            if (exportCheckpointPath == null || writingCheckpoint || session?.Document == null) return;
            IntPtr incoming = IntPtr.Zero, owned = IntPtr.Zero;
            try
            {
                incoming = Marshal.GetIUnknownForObject(document);
                owned = Marshal.GetIUnknownForObject(session.Document);
                if (incoming != owned) return; // Never inspect an unrelated document, even in the owned application.
                if (legacyCheckpoint && document.SaveFormat == (int)Wd.WdSaveFormat.wdFormatDocument) return;
                // Office supplies this document to its connection-point callback on an RPC thread.
                // Read only that event argument (confirmed owned by COM identity), then write its
                // immutable serialization. Never call the held-session lifecycle from this callback
                // or synchronously invoke the UI which may itself be waiting in Word.Save().
                WriteFormattedCheckpoint(document.WordOpenXML);
                if (legacyCheckpoint)
                {
                    legacyCheckpointError = null;
                    AppLogger.Write("Legacy export: preserved current modern formatted state before native save");
                }
            }
            catch (Exception ex)
            {
                if (legacyCheckpoint) legacyCheckpointError = ex.Message;
                else cancel = true; // Existing opt-in plain-text contract; never used by legacy output.
                AppLogger.Write(legacyCheckpoint ? "Legacy export preservation failed; native Save As was not cancelled" : "Training export checkpoint failed; native save cancelled", ex);
                try { ExportCheckpointFailed?.Invoke(ex.Message); }
                catch (Exception notificationError) { AppLogger.Write("Unable to report cancelled export save", notificationError); }
            }
            finally
            {
                if (incoming != IntPtr.Zero) Marshal.Release(incoming);
                if (owned != IntPtr.Zero) Marshal.Release(owned);
            }
        }

        public string SaveFormattedCheckpoint()
        {
            EnsureUsable();
            if (!IsOpened || exportCheckpointPath == null) throw new InvalidOperationException("No formatted export checkpoint is configured.");
            return WriteFormattedCheckpoint(session.Document.WordOpenXML);
        }

        private string WriteFormattedCheckpoint(string serializedDocument)
        {
            if (writingCheckpoint) throw new InvalidOperationException("A checkpoint is already being written.");
            writingCheckpoint = true;
            string temporary = Path.Combine(Path.GetDirectoryName(exportCheckpointPath), ".export-" + Guid.NewGuid().ToString("N") + ".docx");
            try
            {
                // Word serializes the held live document, including unsaved formatting. No SaveAs, reopen,
                // conversion or second application is used; the learner-visible document is unchanged.
                XNamespace pkg = "http://schemas.microsoft.com/office/2006/xmlPackage";
                XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
                var flat = XDocument.Parse(serializedDocument, LoadOptions.PreserveWhitespace);
                var parts = flat.Root?.Elements(pkg + "part").ToList();
                if (flat.Root?.Name != pkg + "package" || parts == null || !parts.Any(p => (string)p.Attribute(pkg + "name") == "/word/document.xml"))
                    throw new InvalidDataException("Word did not provide a complete formatted package.");
                if (parts.Any(p => ((string)p.Attribute(pkg + "contentType") ?? "").IndexOf("macro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ((string)p.Attribute(pkg + "name") ?? "").IndexOf("vba", StringComparison.OrdinalIgnoreCase) >= 0))
                    throw new InvalidDataException("Macro packages are not supported as Training checkpoints.");
                var types = new XDocument(new XElement(ct + "Types"));
                using (var file = File.Create(temporary))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var part in parts)
                    {
                        string name = (string)part.Attribute(pkg + "name"), type = (string)part.Attribute(pkg + "contentType");
                        if (string.IsNullOrEmpty(name) || !name.StartsWith("/", StringComparison.Ordinal) || name.Contains("\\") ||
                            name.Split('/').Any(s => s == ".." || s == ".") || !names.Add(name) || string.IsNullOrEmpty(type))
                            throw new InvalidDataException("Word returned an invalid package part.");
                        if (name == "/word/document.xml") type = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
                        types.Root.Add(new XElement(ct + "Override", new XAttribute("PartName", name), new XAttribute("ContentType", type)));
                        using (var output = zip.CreateEntry(name.Substring(1)).Open())
                        {
                            var xml = part.Element(pkg + "xmlData")?.Elements().SingleOrDefault();
                            var binary = part.Element(pkg + "binaryData");
                            if (xml != null) new XDocument(new XElement(xml)).Save(output);
                            else if (binary != null) { var bytes = Convert.FromBase64String(binary.Value); output.Write(bytes, 0, bytes.Length); }
                            else throw new InvalidDataException("Word returned an empty package part.");
                        }
                    }
                    using (var output = zip.CreateEntry("[Content_Types].xml").Open()) types.Save(output);
                }
                if (File.Exists(exportCheckpointPath))
                {
                    if ((File.GetAttributes(exportCheckpointPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Checkpoint cannot be a reparse point.");
                    File.Replace(temporary, exportCheckpointPath, null);
                }
                else File.Move(temporary, exportCheckpointPath);
                return exportCheckpointPath;
            }
            finally { writingCheckpoint = false; if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
