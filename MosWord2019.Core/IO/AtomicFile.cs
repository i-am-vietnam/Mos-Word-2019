using System;
using System.IO;
using System.Threading;
using MosWord2019.Core.Diagnostics;
namespace MosWord2019.Core.IO
{
    /// <summary>Same-volume atomic commit with a bounded transient Windows filesystem retry budget.</summary>
    public static class AtomicFile
    {
        public static void Commit(string source, string destination, bool replace, Action validate = null, Action<string> log = null, bool ignoreMetadataErrors = false)
        {
            Commit(source, destination, replace, (s, d, r) => { if (r) File.Replace(s, d, null, ignoreMetadataErrors); else File.Move(s, d); },
                File.Exists, Thread.Sleep, () => { RejectReparse(source); RejectReparse(destination); validate?.Invoke(); }, log ?? (s => AppLogger.Write(s)));
        }
        private static void RejectReparse(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Atomic commit cannot traverse a reparse point: " + current);
        }
        internal static void Commit(string source, string destination, bool replace, Action<string, string, bool> operation,
            Func<string, bool> exists, Action<int> delay, Action validate, Action<string> log)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { validate(); operation(source, destination, replace); return; }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xffff;
                    bool transient = code == 32 || code == 33 || code == 1175 || code == 1176 || code == 1177;
                    // A partial Windows replacement can consume the source; never replay an uncertain commit.
                    if (!transient || attempt == 10 || !exists(source))
                    { log("Atomic commit failed after " + attempt + " attempt(s): " + destination + " error=" + code); throw; }
                    delay(220); // Nine waits total 1.98 seconds; no per-attempt log storm.
                }
            }
        }
    }
}
