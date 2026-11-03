using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Server.Data;
using MosWord2019.Server.Packages;
using MosWord2019.Server.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Server.ResultHandling;

public sealed record ValidatedResult(ExamResult Score, DateTimeOffset FinalizedAt, long ElapsedMilliseconds,
    bool ClockRollback, string Language, ExamDefinition Definition, ExamSnapshot Snapshot);

public static class ResultValidation
{
    public static JObject Parse(byte[] bytes)
    {
        using var text = new StringReader(new System.Text.UTF8Encoding(false, true).GetString(bytes));
        using var reader = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("Trailing result data.");
        return value;
    }

    public static DateTimeOffset FinalizedAt(JObject payload)
    {
        var token = payload["completedUtc"];
        if (token?.Type != JTokenType.String || !DateTimeOffset.TryParse((string?)token, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value) || value.Offset != TimeSpan.Zero)
            throw new InvalidDataException("A UTC completion timestamp is required.");
        return value;
    }

    public static ValidatedResult Validate(Session session, JObject payload, DateTimeOffset now)
    {
        if ((int?)payload["version"] != 1 || session.ExamSeed == null || session.ExamSnapshotJson == null ||
            (long?)payload["seed"] != session.ExamSeed || session.ModeSelectedAt == null)
            throw new InvalidDataException("Unsupported or unpinned result.");
        var snapshot = JsonConvert.DeserializeObject<ExamSnapshot>(session.ExamSnapshotJson)
            ?? throw new InvalidDataException("Missing pinned exam.");
        var definition = ServerExamPool.Definition(snapshot, session.ExamSeed.Value);
        if (ExamDefinitionFingerprint.Compute(definition) != session.ExamDefinitionHash)
            throw new InvalidDataException("Pinned exam integrity failed.");
        var supplied = ServerExamPool.Definition(new ExamSnapshot([], [], [], payload["definition"] as JObject
            ?? throw new InvalidDataException("Missing exam definition.")), session.ExamSeed.Value);
        if (ExamDefinitionFingerprint.Compute(supplied) != session.ExamDefinitionHash)
            throw new InvalidDataException("Result definition differs from the pinned exam.");
        var finalized = FinalizedAt(payload);
        if (finalized > now || finalized > SessionTiming.Deadline(session))
            throw new InvalidDataException("Completion exceeds the server deadline.");
        var elapsed = payload["elapsedTicks"];
        if (elapsed?.Type != JTokenType.Integer || (long)elapsed < 0 || (long)elapsed > TimeSpan.FromDays(1).Ticks)
            throw new InvalidDataException("Invalid elapsed time.");
        if (payload["clockRollbackDetected"]?.Type != JTokenType.Boolean)
            throw new InvalidDataException("Missing clock rollback observation.");
        string language = (string?)payload["language"] ?? "";
        if (language is not ("vi" or "en")) throw new InvalidDataException("Invalid result language.");
        var tasks = payload["tasks"] as JArray ?? throw new InvalidDataException("Missing task outcomes.");
        if (tasks.Count != definition.N) throw new InvalidDataException("Every selected task needs an outcome.");
        var expected = definition.Projects.ToDictionary(p => p.ProjectId, StringComparer.Ordinal);
        var grades = new List<ExamTaskGrade>();
        foreach (var task in tasks)
        {
            string project = (string?)task["projectId"] ?? "", id = (string?)task["taskId"] ?? "";
            string outcome = (string?)task["outcome"] ?? "";
            if (!expected.TryGetValue(project, out var selected) || !selected.InstructionKeys.TryGetValue(id, out var key) ||
                (string?)task["instructionKey"] != key || outcome is not ("Pass" or "Fail" or "Error"))
                throw new InvalidDataException("Task evidence differs from the pinned exam.");
            grades.Add(new ExamTaskGrade(project, id, new TaskGradeResult(Enum.Parse<TaskGradeOutcome>(outcome), "", "", id)));
        }
        return new(new ExamScorer(_ => { }).Score(definition, grades), finalized, (long)elapsed / TimeSpan.TicksPerMillisecond,
            (bool)payload["clockRollbackDetected"]!, language, definition, snapshot);
    }

    public static async Task<string> WrongTasksAsync(ValidatedResult result, ServerDbContext db, PackageStorage storage, CancellationToken ct,
        Action<string>? readingPackage = null)
    {
        var wrong = new JArray();
        if (result.Score.WrongTasks.Count == 0) return "[]";
        var packages = await db.Packages.AsNoTracking().Where(p => result.Snapshot.PackageIds.Contains(p.Id)).ToListAsync(ct);
        foreach (var project in result.Definition.Projects)
        {
            var failures = result.Score.WrongTasks.Where(t => t.ProjectCode == ProjectCode(project.ProjectId)).ToList();
            if (failures.Count == 0) continue;
            readingPackage?.Invoke(project.ProjectId);
            var package = packages.SingleOrDefault(p => p.Code == project.ProjectId)
                ?? throw new InvalidDataException("The pinned package is unavailable.");
            var instructions = Instructions(storage, package, result.Language);
            foreach (var failure in failures)
            {
                string instruction = (string?)instructions[failure.InstructionKey]
                    ?? throw new InvalidDataException("A pinned task instruction is unavailable.");
                wrong.Add(new JObject { ["projectId"] = project.ProjectId, ["taskId"] = failure.TaskId,
                    ["instructionKey"] = failure.InstructionKey, ["instruction"] = instruction });
            }
        }
        return wrong.ToString(Formatting.None);
    }

    private static string ProjectCode(string projectId) => projectId.StartsWith("Word2019_", StringComparison.Ordinal)
        ? projectId["Word2019_".Length..] : projectId;

    private static JObject Instructions(PackageStorage storage, Package package, string language)
    {
        byte[] blob = storage.ReadBlob(package), key = storage.ReadKey(package), zip = new byte[blob.Length - 36];
        try
        {
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(blob.AsSpan(8, 12), blob.AsSpan(36), blob.AsSpan(20, 16), zip, PackageStorage.AdditionalData(package));
            if (PackageStorage.Hash(zip) != package.ZipSha256) throw new InvalidDataException("Pinned ZIP integrity failed.");
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entry = archive.GetEntry("lang/" + language + ".json") ?? throw new InvalidDataException("Pinned language is missing.");
            if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Instruction data exceeds its size limit.");
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            if (buffer.Length > 4 * 1024 * 1024) throw new InvalidDataException("Instruction data exceeds its size limit.");
            return PackageJson.Parse(buffer.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(zip); }
    }
}
