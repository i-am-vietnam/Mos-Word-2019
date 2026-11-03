using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Pages.Admin.Audit;

public sealed class IndexModel(ServerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Account { get; set; }
    [BindProperty(SupportsGet = true)] public string? ActionFilter { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public List<Row> Entries { get; private set; } = [];
    public bool HasNext { get; private set; }
    public sealed record Row(DateTimeOffset CreatedAt, string? Username, string Action);
    public async Task OnGetAsync(CancellationToken ct)
    {
        PageNumber = Math.Clamp(PageNumber, 1, 1_000_000);
        Account = Account?.Trim(); ActionFilter = ActionFilter?.Trim();
        if (Account?.Length > 64) Account = Account[..64];
        if (ActionFilter?.Length > 128) ActionFilter = ActionFilter[..128];
        var query = from e in db.AuditLog.AsNoTracking() join a in db.Accounts on e.AccountId equals a.Id into accounts
                    from a in accounts.DefaultIfEmpty() select new { Entry = e, Account = a };
        if (!string.IsNullOrEmpty(Account)) { var normalized = Account.ToUpperInvariant(); query = query.Where(x => x.Account != null && x.Account.NormalizedUsername.Contains(normalized)); }
        if (!string.IsNullOrEmpty(ActionFilter)) query = query.Where(x => x.Entry.Action == ActionFilter);
        Entries = await query.OrderByDescending(x => x.Entry.CreatedAt).ThenByDescending(x => x.Entry.Id)
            .Skip((PageNumber - 1) * 50).Take(51).Select(x => new Row(x.Entry.CreatedAt, x.Account == null ? null : x.Account.Username, x.Entry.Action)).ToListAsync(ct);
        HasNext = Entries.Count > 50;
        if (HasNext) Entries.RemoveAt(50);
    }
}
