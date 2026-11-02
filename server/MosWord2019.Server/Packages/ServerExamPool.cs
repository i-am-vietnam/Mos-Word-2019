using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Server.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Server.Packages;

public sealed record ExamPoolEntry(ProjectMeta Meta, List<TaskDefinition> Tasks);
public sealed record ExamSnapshot(List<Guid> PackageIds, List<TestingCatalogEntry> Catalog, List<ExamPoolEntry> ExamPool, JObject? Definition = null);
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
        snapshot = snapshot with { Definition = JObject.FromObject(definition) };
        return new ServerExam(seed, ExamDefinitionFingerprint.Compute(definition), JsonConvert.SerializeObject(snapshot));
    }

    public static ExamDefinition Definition(ExamSnapshot snapshot, long seed)
    {
        // Older pinned sessions may lack a definition; compose once on the server from their immutable snapshot.
        if (snapshot.Definition == null)
            return ExamComposer.Compose(snapshot.Catalog, snapshot.ExamPool.Select(p => new ProjectPackage { Meta = p.Meta, Tasks = p.Tasks }), seed);
        var value = snapshot.Definition;
        var projects = (JArray?)value["Projects"] ?? throw new InvalidDataException("Missing pinned definition.");
        var definition = new ExamDefinition((long)value["Seed"]!, projects.Select(p => new ExamProjectDefinition((string)p["ProjectId"]!,
            ((JArray)p["TaskIds"]!).Select(id => new TaskDefinition { TaskId = (string)id!, InstructionKey = (string)p["InstructionKeys"]![(string)id!]! }))));
        if (definition.Seed != seed || definition.N != (int)value["N"]!) throw new InvalidDataException("Invalid pinned definition.");
        return definition;
    }
}
