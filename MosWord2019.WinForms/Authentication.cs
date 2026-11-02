using System;
using System.IO;
using System.Text;

namespace MosWord2019
{
    public interface IAuthenticationService
    {
        bool Authenticate(string username, string password);
    }

#if DEBUG
    public sealed class OfflineAuthenticationService : IAuthenticationService
    {
        public bool Authenticate(string username, string password) =>
            !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }
#endif

    // Optional UI preferences only. Credentials and exam state never belong here.
    internal sealed class LoginPreferences
    {
        private readonly string path;
        internal string Username { get; set; } = "";
        internal string Language { get; set; } = "en";
        internal LoginPreferences(string path) { this.path = path; }
        internal void Load()
        {
            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 2 && (lines[0] == "en" || lines[0] == "vi"))
                { Username = Encoding.UTF8.GetString(Convert.FromBase64String(lines[1])); Language = lines[0]; }
            }
            catch (Exception) { /* Preferences are optional; startup must remain available. */ }
        }
        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, new[] { Language, Convert.ToBase64String(Encoding.UTF8.GetBytes(Username)) }, Encoding.UTF8);
            }
            catch (Exception) { /* An unwritable preferences file must not block login. */ }
        }
    }
}
