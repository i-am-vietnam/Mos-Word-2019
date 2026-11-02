using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Server.Data;
using Newtonsoft.Json;

namespace MosWord2019.Server.Packages;

public sealed class PackageImporter(ServerDbContext database, PackageStorage storage, TimeProvider time)
{
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    private static readonly Regex CodePattern = new("^Word2019_P[0-9]{2,}$", RegexOptions.CultureInvariant);

    public async Task<int> ImportAsync(string projectsRoot, CancellationToken cancellationToken = default)
    {
        projectsRoot = Path.GetFullPath(projectsRoot);
        PackageStorage.RejectReparsePoints(projectsRoot);
        var catalogPath = Path.Combine(projectsRoot, "testing-catalog.json");
        var catalog = ReadJson<List<TestingCatalogEntry>>(catalogPath);
        var prepared = new List<(ProjectPackage Project, TestingCatalogEntry Catalog, string[] Files)>();
        foreach (var directory in Directory.EnumerateDirectories(projectsRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageStorage.RejectReparsePoints(directory);
            var code = Path.GetFileName(directory);
            if (!CodePattern.IsMatch(code)) throw new InvalidDataException("Unexpected package directory: " + code);
            var files = EnumerateFiles(directory);
            var meta = ReadJson<ProjectMeta>(Path.Combine(directory, "meta.json"));
            if (meta.ProjectId != code) throw new InvalidDataException("Package identity does not match its directory.");
            var starter = SafeEntry(meta.Starter);
            if (!files.Contains(Path.Combine(directory, starter.Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Package starter does not exist.");
            var tasks = ReadJson<List<TaskDefinition>>(Path.Combine(directory, "tasks.json"));
            var entry = catalog.SingleOrDefault(x => x.ProjectId == code) ?? throw new InvalidDataException("Missing catalog entry.");
            prepared.Add((new ProjectPackage { Meta = meta, Tasks = tasks, ProjectFolderPath = directory }, entry, files));
        }
        // Validate the entire candidate pool before creating any package or database row.
        _ = new ExamComposer(catalog, prepared.Select(x => x.Project)).Compose(0);
        var created = 0;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Serialize filesystem writes and reconciliation across all importer processes.
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(740219201901)", cancellationToken);
        storage.RemoveUnreferenced((await database.Packages.Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet());
        foreach (var item in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zip = CreateZip(item.Project.ProjectFolderPath, item.Files);
            var version = PackageStorage.Hash(zip);
            var existing = await database.Packages.SingleOrDefaultAsync(x => x.Code == item.Project.Meta.ProjectId && x.Version == version, cancellationToken);
            if (existing != null)
            {
                // Catalog policy is outside the ZIP; active exams retain their pinned pool snapshot.
                existing.CatalogEntryJson = JsonConvert.SerializeObject(item.Catalog);
                continue;
            }
            var package = new Package
            {
                Code = item.Project.Meta.ProjectId, Version = version, ZipSha256 = version,
                CreatedAt = time.GetUtcNow(), MetadataJson = JsonConvert.SerializeObject(item.Project.Meta),
                TasksJson = JsonConvert.SerializeObject(item.Project.Tasks.Select(x => new { x.TaskId, x.InstructionKey })),
                CatalogEntryJson = JsonConvert.SerializeObject(item.Catalog)
            };
            storage.Store(package, zip);
            database.Packages.Add(package);
            created++;
        }
        database.AuditLog.Add(new AuditEntry { Action = "PackagesImported", CreatedAt = time.GetUtcNow(), DetailsJson = JsonConvert.SerializeObject(new { created }) });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    private static T ReadJson<T>(string path)
    {
        PackageStorage.RejectReparsePoints(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Package metadata exceeds its size limit.");
        return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), new JsonSerializerSettings { MaxDepth = 64, TypeNameHandling = TypeNameHandling.None })
            ?? throw new InvalidDataException("Package metadata is null.");
    }

    private static string[] EnumerateFiles(string directory)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        long total = 0;
        while (pending.Count != 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                PackageStorage.RejectReparsePoints(entry);
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                SafeEntry(Path.GetRelativePath(directory, entry).Replace(Path.DirectorySeparatorChar, '/'));
                total = checked(total + new FileInfo(entry).Length);
                files.Add(entry);
                if (total > MaximumExpandedBytes || files.Count > 1000) throw new InvalidDataException("Package exceeds extraction limits.");
            }
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static byte[] CreateZip(string root, string[] files)
    {
        using var output = new MemoryStream();
        long total = 0;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var path in files)
            {
                PackageStorage.RejectReparsePoints(path);
                var entry = zip.CreateEntry(SafeEntry(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')), CompressionLevel.SmallestSize);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                total = checked(total + source.Length);
                if (total > MaximumExpandedBytes) throw new InvalidDataException("Package grew beyond its extraction limit.");
                using var destination = entry.Open();
                source.CopyTo(destination);
                if (output.Length > PackageStorage.MaximumBlobBytes - 36) throw new InvalidDataException("Compressed package exceeds its download limit.");
            }
        }
        if (output.Length > PackageStorage.MaximumBlobBytes - 36) throw new InvalidDataException("Compressed package exceeds its download limit.");
        return output.ToArray();
    }

    private static string SafeEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') ||
            path.Split('/').Any(x => x.Length == 0 || x == "." || x == ".." || x.EndsWith('.') || x.EndsWith(' ')))
            throw new InvalidDataException("Unsafe package entry path.");
        return path;
    }
}
