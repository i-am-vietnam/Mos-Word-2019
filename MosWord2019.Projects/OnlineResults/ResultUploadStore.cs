using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using MosWord2019.Core.IO;
using MosWord2019.Projects.OnlinePackages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Projects.OnlineResults
{
    /// <summary>Bounded CurrentUser DPAPI records. No plaintext credentials or payload staging files.</summary>
    internal sealed class ResultUploadStore
    {
        internal const int MaximumPayloadBytes = 256 * 1024;
        private const int MaximumRecordBytes = 2 * 1024 * 1024;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MosWord2019.ResultUpload.v1");
        private readonly string root;
        internal ResultUploadStore(string root)
        {
            this.root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosWord2019", "ResultUploads"));
            // Online credentials never belong in a checkout, including with a custom path.
            for (string p = this.root; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                if (Directory.Exists(Path.Combine(p, ".git")) || File.Exists(Path.Combine(p, ".git"))) throw new ArgumentException("The upload store must be outside a repository.", nameof(root));
            OnlinePackageCache.CreatePrivateDirectory(this.root);
            var security = new DirectorySecurity(); var user = WindowsIdentity.GetCurrent().User;
            security.SetOwner(user); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(this.root, security);
        }
        internal FileStream AcquirePumpLease() { return OpenLease(".upload-pump-lease"); }
        internal FileStream AcquireLease()
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return OpenLease(".upload-lease"); }
                catch (IOException ex) when (attempt < 9 && ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33))
                { System.Threading.Thread.Sleep(220); }
            }
        }
        private FileStream OpenLease(string name)
        {
            string path = Path.Combine(root, name); OnlinePackageCache.RejectReparse(path);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        private string PathFor(Guid id) { return Path.Combine(root, id.ToString("N") + ".upload"); }
        private string ReceiptPath(Guid id) { return Path.Combine(root, "Receipts", id.ToString("N") + ".receipt"); }
        internal void ArchiveReceipt(Guid id)
        {
            string source = PathFor(id), target = ReceiptPath(id);
            OnlinePackageCache.CreatePrivateDirectory(Path.GetDirectoryName(target));
            AtomicFile.Commit(source, target, false, () => { OnlinePackageCache.RejectReparse(root); OnlinePackageCache.RejectReparse(target); });
        }
        internal Guid[] Ids()
        {
            OnlinePackageCache.RejectReparse(root);
            var files = Directory.EnumerateFiles(root, "*.upload", SearchOption.TopDirectoryOnly).Take(1025).ToArray();
            if (files.Length > 1024) throw new InvalidDataException("Upload queue limit exceeded.");
            return files.Select(p => { Guid id; return Guid.TryParseExact(Path.GetFileNameWithoutExtension(p), "N", out id) ? id : Guid.Empty; }).Where(id => id != Guid.Empty).ToArray();
        }
        internal JObject Read(Guid id)
        {
            string path = PathFor(id); OnlinePackageCache.RejectReparse(path);
            if (!File.Exists(path)) path = ReceiptPath(id);
            OnlinePackageCache.RejectReparse(path);
            if (!File.Exists(path)) return null;
            byte[] encrypted = ReadBounded(path, MaximumRecordBytes);
            byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return Parse(plain); } finally { Array.Clear(plain, 0, plain.Length); }
        }
        internal void Write(Guid id, JObject data)
        {
            byte[] plain = Encoding.UTF8.GetBytes(data.ToString(Formatting.None));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(plain, 0, plain.Length); }
            if (encrypted.Length > MaximumRecordBytes) throw new InvalidDataException("Upload record exceeds limit.");
            string target = PathFor(id), temp = Path.Combine(root, ".upload-" + Guid.NewGuid().ToString("N") + ".tmp");
            OnlinePackageCache.RejectReparse(target);
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
                AtomicFile.Commit(temp, target, File.Exists(target), () => { OnlinePackageCache.RejectReparse(root); OnlinePackageCache.RejectReparse(target); });
            }
            finally { OnlinePackageCache.RejectReparse(temp); if (File.Exists(temp)) File.Delete(temp); }
        }
        internal static byte[] ReadBounded(string path, int maximum)
        {
            OnlinePackageCache.RejectReparse(path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length == 0 || input.Length > maximum) throw new InvalidDataException("Invalid result file size.");
                var bytes = new byte[(int)input.Length]; int position = 0, count;
                while (position < bytes.Length && (count = input.Read(bytes, position, bytes.Length - position)) > 0) position += count;
                if (position != bytes.Length) throw new EndOfStreamException();
                return bytes;
            }
        }
        internal static JObject Parse(byte[] bytes)
        {
            using (var text = new StringReader(new UTF8Encoding(false, true).GetString(bytes)))
            using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 })
            {
                var data = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("Trailing JSON content.");
                return data;
            }
        }
    }
}
