using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Server.Data;
using Newtonsoft.Json;

namespace MosWord2019.Server.Packages;

public sealed record ExamPoolEntry(ProjectMeta Meta, List<TaskDefinition> Tasks);
public sealed record ExamSnapshot(List<Guid> PackageIds, List<TestingCatalogEntry> Catalog, List<ExamPoolEntry> ExamPool);
public sealed record ServerExam(long Seed, string DefinitionHash, string SnapshotJson);

public interface IServerExamPool
{
    Task<ServerExam> CreateAsync(CancellationToken ct);
}

public sealed class ServerExamPool(ServerDbContext db) : IServerExamPool
{
    public static async Task<List<Package>> LatestAsync(ServerDbContext db, CancellationToken ct) =>
        (await db.Packages.AsNoTracking().OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ct))
            .GroupBy(p => p.Code, StringComparer.Ordinal).Select(g => g.First()).OrderBy(p => p.Code, StringComparer.Ordinal).ToList();

    public static ExamSnapshot Snapshot(List<Package> packages) => new([], packages.Select(p =>
        JsonConvert.DeserializeObject<TestingCatalogEntry>(p.CatalogEntryJson) ?? throw new InvalidDataException("Missing catalog.")).ToList(),
        packages.Select(p => new ExamPoolEntry(
            JsonConvert.DeserializeObject<ProjectMeta>(p.MetadataJson) ?? throw new InvalidDataException("Missing metadata."),
            JsonConvert.DeserializeObject<List<TaskDefinition>>(p.TasksJson) ?? throw new InvalidDataException("Missing tasks."))).ToList());

    public async Task<ServerExam> CreateAsync(CancellationToken ct)
    {
        var packages = await LatestAsync(db, ct);
        ExamSnapshot snapshot;
        try { snapshot = Snapshot(packages); }
        catch (JsonException ex) { throw new InvalidDataException("The imported exam pool is invalid.", ex); }
        long seed = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));
        var definition = ExamComposer.Compose(snapshot.Catalog, snapshot.ExamPool.Select(p =>
            new ProjectPackage { Meta = p.Meta, Tasks = p.Tasks }), seed);
        var byCode = packages.ToDictionary(p => p.Code, StringComparer.Ordinal);
        snapshot.PackageIds.AddRange(definition.Projects.Select(p => byCode[p.ProjectId].Id));
        return new ServerExam(seed, ExamDefinitionFingerprint.Compute(definition), JsonConvert.SerializeObject(snapshot));
    }
}
