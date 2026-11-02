using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Pages.Admin.Accounts;

public sealed class SessionsModel(ServerDbContext db) : PageModel
{
    public string Username { get; private set; } = "";
    public Guid AccountId { get; private set; }
    public List<SessionSummary> Sessions { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public bool HasNext { get; private set; }
    public sealed record SessionSummary(Guid Id, Guid AccountId, DateTimeOffset StartedAt, string? Mode,
        DateTimeOffset? EndedAt, DateTimeOffset? LastHeartbeatAt, string MachinePrefix);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.IsAdmin, cancellationToken);
        if (account == null) return NotFound();
        Username = account.Username;
        AccountId = id;
        PageNumber = Math.Clamp(PageNumber, 1, 1_000_000);
        Sessions = await db.Sessions.AsNoTracking().Where(x => x.AccountId == id)
            .OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).Skip((PageNumber - 1) * 50).Take(51)
            .Select(x => new SessionSummary(x.Id, x.AccountId, x.StartedAt, x.Mode, x.EndedAt,
                x.LastHeartbeatAt, x.MachineId.Substring(0, 12))).ToListAsync(cancellationToken);
        HasNext = Sessions.Count > 50;
        if (HasNext) Sessions.RemoveAt(50);
        return Page();
    }
}
