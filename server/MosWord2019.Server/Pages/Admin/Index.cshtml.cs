using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Pages.Admin;

public sealed class IndexModel(ServerDbContext db, TimeProvider time) : PageModel
{
    public int Learners { get; private set; }
    public int LearnersWithUses { get; private set; }
    public int ActiveSessions { get; private set; }
    public int ResultsToday { get; private set; }
    public double? WeeklyAverage { get; private set; }
    public List<Results.IndexModel.Summary> Latest { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(AdminDisplay.Local(now));
        var start = AdminDisplay.DayStart(today);
        var week = AdminDisplay.DayStart(today.AddDays(-((7 + (int)today.DayOfWeek - (int)DayOfWeek.Monday) % 7)));
        Learners = await db.Accounts.CountAsync(a => !a.IsAdmin, ct);
        LearnersWithUses = await db.Accounts.CountAsync(a => !a.IsAdmin && a.Used < a.MaxUses, ct);
        var pending = await (from s in db.Sessions.AsNoTracking() join a in db.Accounts.AsNoTracking() on s.AccountId equals a.Id
                             where s.EndedAt == null select new { Session = s, Account = a }).ToListAsync(ct);
        ActiveSessions = pending.Count(x => SessionPolicy.Evaluate(x.Account, x.Session, now, x.Session.AccountCredentialVersion).Allowed);
        ResultsToday = await db.Results.CountAsync(r => r.ReceivedAt >= start && r.ReceivedAt < start.AddDays(1), ct);
        WeeklyAverage = await db.Results.Where(r => r.ReceivedAt >= week && r.ReceivedAt <= now).AverageAsync(r => (double?)r.Score, ct);
        Latest = await (from r in db.Results.AsNoTracking() join s in db.Sessions on r.SessionId equals s.Id
                        join a in db.Accounts on s.AccountId equals a.Id orderby r.ReceivedAt descending, r.Id
                        select new Results.IndexModel.Summary(r.Id, a.Id, a.Username, r.ReceivedAt, r.Score, r.Passed, r.FlagsJson)).Take(10).ToListAsync(ct);
    }
}
