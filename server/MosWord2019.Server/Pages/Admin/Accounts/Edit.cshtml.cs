using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Pages.Admin.Accounts;

public sealed class EditModel(ServerDbContext db, AccountAdministration administration) : PageModel
{
    public string Username { get; private set; } = "";
    public int Used { get; private set; }
    public int CurrentMaxUses { get; private set; }
    [BindProperty, Range(0, int.MaxValue, ErrorMessage = "Số lượt tối đa phải từ 0 đến 2147483647.")] public int? MaxUses { get; set; }
    [BindProperty, Range(0, int.MaxValue, ErrorMessage = "Số lượt cấp thêm phải từ 0 đến 2147483647.")] public int AddUses { get; set; }
    [BindProperty] public DateTime? ExpiresUtc { get; set; }
    [BindProperty] public bool Locked { get; set; }
    [BindProperty, DataType(DataType.Password)] public string? NewPassword { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await LoadAsync(id, cancellationToken);
        if (account == null) return NotFound();
        ExpiresUtc = account.ExpiresAt.HasValue ? AdminDisplay.Local(account.ExpiresAt.Value) : null;
        Locked = account.Locked;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var password = NewPassword;
        NewPassword = null;
        ModelState.Remove(nameof(NewPassword));
        if (password != null && !AccountAdministration.ValidPassword(password))
            ModelState.AddModelError(nameof(NewPassword), "Mật khẩu phải có 12–128 ký tự và không được để trống.");
        if (await LoadAsync(id, cancellationToken) == null) return NotFound();
        if (!ModelState.IsValid) return Page();
        var expiry = ExpiresUtc.HasValue ? AdminDisplay.Utc(ExpiresUtc.Value) : (DateTimeOffset?)null;
        var result = await administration.UpdateAsync(id, MaxUses, AddUses, expiry, Locked, password, cancellationToken);
        if (result.Success) return RedirectToPage(new { id });
        ModelState.AddModelError("", AdminDisplay.Error(result.Error));
        return Page();
    }

    private async Task<Account?> LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && !x.IsAdmin, cancellationToken);
        if (account != null) { Username = account.Username; Used = account.Used; CurrentMaxUses = account.MaxUses; }
        return account;
    }
}
