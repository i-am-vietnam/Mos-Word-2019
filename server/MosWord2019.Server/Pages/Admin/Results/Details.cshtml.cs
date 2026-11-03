using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.ResultHandling;

namespace MosWord2019.Server.Pages.Admin.Results;

public sealed class DetailsModel(ServerDbContext db) : PageModel
{
    public Result Result { get; private set; } = null!;
    public Guid AccountId { get; private set; }
    public string Username { get; private set; } = "";
    public List<WrongTask> WrongTasks { get; private set; } = [];
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public List<ProjectSummary> Projects { get; private set; } = [];
    public List<Gap> Gaps { get; private set; } = [];
    public sealed record ProjectSummary(string ProjectId, int Points, int Maximum, int Correct, int Count);
    public sealed record Gap([property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("endedAt")] DateTimeOffset EndedAt, [property: JsonPropertyName("kind")] string Kind);
    public sealed record WrongTask(
        [property: JsonPropertyName("projectId")] string ProjectId,
        [property: JsonPropertyName("taskId")] string TaskId,
        [property: JsonPropertyName("instructionKey")] string InstructionKey,
        [property: JsonPropertyName("instruction")] string Instruction);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var detail = await (from result in db.Results.AsNoTracking()
                            join session in db.Sessions on result.SessionId equals session.Id
                            join account in db.Accounts on session.AccountId equals account.Id
                            where result.Id == id
                            select new { Result = result, Session = session, AccountId = account.Id, account.Username })
            .SingleOrDefaultAsync(cancellationToken);
        if (detail == null) return NotFound();
        Result = detail.Result;
        AccountId = detail.AccountId;
        Username = detail.Username;
        WrongTasks = JsonSerializer.Deserialize<List<WrongTask>>(Result.WrongTasksJson) ?? [];
        StartedAt = detail.Session.ModeSelectedAt ?? detail.Session.StartedAt;
        Gaps = JsonSerializer.Deserialize<List<Gap>>(Result.OfflineGapsJson) ?? [];
        var payload = ResultValidation.Parse(System.Text.Encoding.UTF8.GetBytes(Result.PayloadJson));
        if (DateTimeOffset.TryParse((string?)payload["completedUtc"], out var completed)) CompletedAt = completed;
        if ((long?)payload["elapsedTicks"] is { } ticks && ticks >= 0 && ticks <= TimeSpan.TicksPerDay) Duration = TimeSpan.FromTicks(ticks);
        // Accepted payloads use the existing scorer; the view never trusts client totals or weights.
        if (detail.Session.ExamSnapshotJson != null && detail.Session.ExamSeed != null)
        {
            var validated = ResultValidation.Validate(detail.Session, payload, Result.ReceivedAt);
            Projects = validated.Score.PerProject.Select(p =>
            {
                var tasks = ((Newtonsoft.Json.Linq.JArray)payload["tasks"]!).Where(t => (string?)t["projectId"] == p.ProjectId).ToList();
                return new ProjectSummary(p.ProjectId, p.Points, p.MaximumPoints, tasks.Count(t => (string?)t["outcome"] == "Pass"), tasks.Count);
            }).ToList();
        }
        return Page();
    }
}
