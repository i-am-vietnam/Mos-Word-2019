using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Pages.Admin.Sessions;

public sealed class IndexModel(ServerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public List<Row> Sessions { get; private set; } = [];
    public bool HasNext { get; private set; }
    public sealed record Row(Guid Id, string Username, string? Mode, DateTimeOffset StartedAt,
        DateTimeOffset? LastHeartbeatAt, DateTimeOffset? EndedAt, string? EndReason, string MachinePrefix);
    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = Math.Clamp(PageNumber, 1, 1_000_000);
        Search = Search?.Trim();
        if (Search?.Length > 64) Search = Search[..64];
        var query = from s in db.Sessions.AsNoTracking() join a in db.Accounts on s.AccountId equals a.Id
                    where !a.IsAdmin select new { Session = s, Account = a };
        if (!string.IsNullOrEmpty(Search)) { var normalized = Search.ToUpperInvariant(); query = query.Where(x => x.Account.NormalizedUsername.Contains(normalized)); }
        Sessions = await query.OrderByDescending(x => x.Session.StartedAt).ThenBy(x => x.Session.Id)
            .Skip((PageNumber - 1) * 50).Take(51).Select(x => new Row(x.Session.Id, x.Account.Username,
                x.Session.Mode, x.Session.StartedAt, x.Session.LastHeartbeatAt, x.Session.EndedAt,
                x.Session.EndReason, x.Session.MachineId.Substring(0, 12))).ToListAsync(ct);
        HasNext = Sessions.Count > 50;
        if (HasNext) Sessions.RemoveAt(50);
    }
}
