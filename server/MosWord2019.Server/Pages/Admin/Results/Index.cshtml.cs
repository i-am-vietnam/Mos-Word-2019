using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Pages.Admin.Results;

public sealed class IndexModel(ServerDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? AccountId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Account { get; set; }
    [BindProperty(SupportsGet = true)] public string? Verdict { get; set; }
    [BindProperty(SupportsGet = true)] public string? Flag { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? FromDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? ToDate { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public string? Username { get; private set; }
    public List<Summary> Results { get; private set; } = [];
    public bool HasNext { get; private set; }
    public sealed record Summary(Guid Id, Guid AccountId, string Username, DateTimeOffset ReceivedAt,
        int Score, bool Passed, string FlagsJson);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || PageNumber < 1 || PageNumber > 1_000_000 ||
            ToDate == DateOnly.MaxValue || FromDate == DateOnly.MinValue ||
            Verdict is not (null or "" or "passed" or "practice") || Flag is not (null or "" or "any" or "TimingInconsistent" or "ClockRollbackDetected") ||
            (FromDate.HasValue && ToDate.HasValue && FromDate > ToDate))
            return BadRequest("Bộ lọc không hợp lệ. Vui lòng kiểm tra tài khoản, ngày, kết quả, cờ và số trang.");
        if (AccountId.HasValue)
        {
            Username = await db.Accounts.AsNoTracking().Where(x => x.Id == AccountId && !x.IsAdmin)
                .Select(x => x.Username).SingleOrDefaultAsync(cancellationToken);
            if (Username == null) return NotFound();
        }
        var query = from result in db.Results.AsNoTracking()
                    join session in db.Sessions on result.SessionId equals session.Id
                    join account in db.Accounts on session.AccountId equals account.Id
                    select new { Result = result, Account = account };
        if (AccountId.HasValue) query = query.Where(x => x.Account.Id == AccountId);
        Account = Account?.Trim();
        if (Account?.Length > 64) Account = Account[..64];
        if (!string.IsNullOrEmpty(Account)) { var normalized = Account.ToUpperInvariant(); query = query.Where(x => x.Account.NormalizedUsername.Contains(normalized)); }
        if (Verdict == "passed") query = query.Where(x => x.Result.Passed);
        if (Verdict == "practice") query = query.Where(x => !x.Result.Passed);
        if (Flag == "any") query = query.Where(x => x.Result.FlagsJson != "[]");
        else if (!string.IsNullOrEmpty(Flag)) query = query.Where(x => EF.Functions.JsonContains(x.Result.FlagsJson, "[\"" + Flag + "\"]"));
        if (FromDate.HasValue)
        {
            var start = AdminDisplay.DayStart(FromDate.Value);
            query = query.Where(x => x.Result.ReceivedAt >= start);
        }
        if (ToDate.HasValue)
        {
            var end = AdminDisplay.DayStart(ToDate.Value.AddDays(1));
            query = query.Where(x => x.Result.ReceivedAt < end);
        }
        Results = await query.OrderByDescending(x => x.Result.ReceivedAt).ThenBy(x => x.Result.Id)
            .Skip((PageNumber - 1) * 50).Take(51)
            .Select(x => new Summary(x.Result.Id, x.Account.Id, x.Account.Username, x.Result.ReceivedAt,
                x.Result.Score, x.Result.Passed, x.Result.FlagsJson)).ToListAsync(cancellationToken);
        HasNext = Results.Count > 50;
        if (HasNext) Results.RemoveAt(50);
        return Page();
    }
}
