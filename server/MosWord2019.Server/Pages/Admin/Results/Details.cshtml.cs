using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Pages.Admin.Results;

public sealed class DetailsModel(ServerDbContext db) : PageModel
{
    public Result Result { get; private set; } = null!;
    public Guid AccountId { get; private set; }
    public string Username { get; private set; } = "";
    public List<WrongTask> WrongTasks { get; private set; } = [];
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
                            select new { Result = result, AccountId = account.Id, account.Username })
            .SingleOrDefaultAsync(cancellationToken);
        if (detail == null) return NotFound();
        Result = detail.Result;
        AccountId = detail.AccountId;
        Username = detail.Username;
        WrongTasks = JsonSerializer.Deserialize<List<WrongTask>>(Result.WrongTasksJson) ?? [];
        return Page();
    }
}
