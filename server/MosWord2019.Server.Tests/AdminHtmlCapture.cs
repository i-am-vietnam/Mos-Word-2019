using System.Text.RegularExpressions;

namespace MosWord2019.Server.Tests;

// Optional visual evidence from synthetic PostgreSQL fixtures, never a live server/account.
internal static class AdminHtmlCapture
{
    public static async Task SaveAsync(HttpClient client, string name, string path)
    {
        var configured = Environment.GetEnvironmentVariable("MOSWORD_ADMIN_CAPTURE_PATH");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md"))) repository = repository.Parent;
        if (repository == null) throw new InvalidOperationException("Cannot locate test repository.");
        var output = Path.GetFullPath(configured);
        if (!output.StartsWith(Path.Combine(repository.FullName, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Admin visual captures must remain under repository artifacts.");
        Directory.CreateDirectory(output);
        var html = await client.GetStringAsync(path);
        html = Regex.Replace(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]*>", "", RegexOptions.CultureInvariant);
        html = html.Replace("href=\"/vendor/", "href=\"vendor/").Replace("href=\"/css/", "href=\"css/");
        await File.WriteAllTextAsync(Path.Combine(output, name + ".html"), html);
        var webroot = Path.Combine(repository.FullName, "server", "MosWord2019.Server", "wwwroot");
        foreach (var relative in new[] { "vendor/bootstrap/bootstrap.min.css", "vendor/bootstrap/LICENSE", "css/admin.css" })
        {
            var target = Path.Combine(output, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(webroot, relative), target, true);
        }
    }
}
