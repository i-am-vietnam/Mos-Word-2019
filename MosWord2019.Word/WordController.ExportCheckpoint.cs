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
        public event Action<string> ExportCheckpointFailed;
        public event Action<string> OwnedDocumentSaving;
        public event Action<string> WorkingFileWritten;

        public void ConfigureExportCheckpoint(string checkpointPath)
        {
            RunCom(() =>
            {
                EnsureUsable();
                if (!IsOpened) throw new InvalidOperationException("No owned document is open.");
                string path = Path.GetFullPath(checkpointPath);
                if (!string.Equals(Path.GetFileName(path), "export-checkpoint.docx", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(session.Document.FullName), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("An export checkpoint must be the exact Training checkpoint beside the owned working document.");
                exportCheckpointPath = path;
                SaveFormattedCheckpoint(); // Establish a valid checkpoint before native learner operations.
            });
        }

        private void BeforeDocumentSave(Wd.Document document, ref bool saveAsUi, ref bool cancel)
        {
            IntPtr incoming = IntPtr.Zero, owned = IntPtr.Zero;
            try
            {
                if (writingCheckpoint || session?.Document == null) return;
                incoming = Marshal.GetIUnknownForObject(document);
                owned = Marshal.GetIUnknownForObject(session.Document);
                if (incoming != owned) return; // Never inspect an unrelated document, even in the owned application.
                OwnedDocumentSaving?.Invoke(document.FullName); // Plain owned facts only; the UI queues the post-save check.
                if (exportCheckpointPath == null) return;
                // COM delivers this connection-point callback through its apartment marshaling.
                // Inspect only the verified event argument. Queue UI work rather than synchronously
                // invoking the UI or calling the held controller's lifecycle during a native save.
                WriteFormattedCheckpoint(document.WordOpenXML);
            }
            catch (Exception ex)
            {
                cancel = true; // Do not let a failed checkpoint precede a lossy save.
                AppLogger.Write("Training export checkpoint failed; native save cancelled", ex);
                try { ExportCheckpointFailed?.Invoke(ex.Message); }
                catch (Exception notificationError) { AppLogger.Write("Unable to report cancelled export save", notificationError); }
            }
            finally
            {
                if (incoming != IntPtr.Zero) Marshal.Release(incoming);
                if (owned != IntPtr.Zero) Marshal.Release(owned);
                // Release only this callback's RCW reference, never FinalReleaseComObject on the held document.
                if (document != null && Marshal.IsComObject(document))
                    try { Marshal.ReleaseComObject(document); }
                    catch (Exception ex) { AppLogger.Write("Unable to release save event document reference", ex); }
            }
        }

        public event Action<string, int, string> OwnedDocumentClosing;
        private void BeforeDocumentClose(Wd.Document document, ref bool cancel)
        {
            IntPtr incoming = IntPtr.Zero, owned = IntPtr.Zero;
            try
            {
                if (OwnedDocumentClosing == null || session?.Document == null || exportCheckpointPath == null) return;
                incoming = Marshal.GetIUnknownForObject(document); owned = Marshal.GetIUnknownForObject(session.Document);
                if (incoming != owned) return;
                testingWindow = IntPtr.Zero; // A closed window handle must never be reused to lock a different document.
                string path = document.FullName; int format = (int)document.SaveFormat;
                string saved = WriteFormattedCheckpoint(document.WordOpenXML);
                OwnedDocumentClosing?.Invoke(path, format, saved); // Plain facts; no held controller call inside this callback.
            }
            catch (Exception ex)
            { cancel = true; AppLogger.Write("Testing close checkpoint failed; close cancelled", ex); }
            finally
            {
                if (incoming != IntPtr.Zero) Marshal.Release(incoming);
                if (owned != IntPtr.Zero) Marshal.Release(owned);
                if (document != null && Marshal.IsComObject(document))
                    try { Marshal.ReleaseComObject(document); } catch (Exception ex) { AppLogger.Write("Unable to release close event argument", ex); }
            }
        }

        public string SaveFormattedCheckpoint()
        {
            return RunCom(() =>
            {
                EnsureUsable();
                if (!IsOpened || exportCheckpointPath == null) throw new InvalidOperationException("No formatted export checkpoint is configured.");
                return WriteFormattedCheckpoint(session.Document.WordOpenXML);
            });
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
                WorkingFileWritten?.Invoke(temporary);
                if (File.Exists(exportCheckpointPath))
                {
                    if ((File.GetAttributes(exportCheckpointPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Checkpoint cannot be a reparse point.");
                    File.Replace(temporary, exportCheckpointPath, null);
                }
                else File.Move(temporary, exportCheckpointPath);
                WorkingFileWritten?.Invoke(exportCheckpointPath);
                return exportCheckpointPath;
            }
            finally { writingCheckpoint = false; if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
