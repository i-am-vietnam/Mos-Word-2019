using System;
using System.IO;

namespace MosWord2019.Core.Diagnostics
{
    public static class AppLogger
    {
        public static void Write(string message, Exception exception = null)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MosWord2019", "Logs");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "training-" + DateTime.Today.ToString("yyyyMMdd") + ".log"),
                    DateTime.Now.ToString("O") + " " + message + (exception == null ? "" : Environment.NewLine + exception) + Environment.NewLine);
            }
            catch (Exception) { /* Diagnostics must not prevent saving or cleanup. */ }
        }
    }
}
