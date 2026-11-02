using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Pages.Admin.Accounts;

public sealed class IndexModel(ServerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public List<Account> Accounts { get; private set; } = [];
    public bool HasNext { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        PageNumber = Math.Clamp(PageNumber, 1, 1_000_000);
        var query = db.Accounts.AsNoTracking().Where(x => !x.IsAdmin);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            Search = Search.Length > 64 ? Search[..64] : Search;
            var search = Search.ToUpperInvariant();
            query = query.Where(x => x.NormalizedUsername.Contains(search));
        }
        Accounts = await query.OrderBy(x => x.NormalizedUsername).Skip((PageNumber - 1) * 50).Take(51)
            .ToListAsync(cancellationToken);
        HasNext = Accounts.Count > 50;
        if (HasNext) Accounts.RemoveAt(50);
    }
}
