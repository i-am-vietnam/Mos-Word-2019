using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Pages.Admin.Accounts;

public sealed class CreateModel(AccountAdministration administration) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Vui lòng nhập tài khoản."), StringLength(64, ErrorMessage = "Tài khoản tối đa 64 ký tự.")] public string Username { get; set; } = "";
    [BindProperty, DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty, Range(0, int.MaxValue, ErrorMessage = "Số lượt tối đa phải từ 0 đến 2147483647.")] public int MaxUses { get; set; }
    [BindProperty] public DateTime? ExpiresUtc { get; set; }
    [BindProperty] public bool Locked { get; set; }

    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var password = Password;
        Password = "";
        ModelState.Remove(nameof(Password));
        if (!AccountAdministration.ValidPassword(password))
            ModelState.AddModelError(nameof(Password), "Mật khẩu phải có 12–128 ký tự và không được để trống.");
        if (!ModelState.IsValid) return Page();
        var expiry = ExpiresUtc.HasValue ? AdminDisplay.Utc(ExpiresUtc.Value) : (DateTimeOffset?)null;
        var result = await administration.CreateAsync(Username, password, MaxUses, expiry, Locked, cancellationToken);
        if (result.Success) return RedirectToPage("Edit", new { id = result.AccountId });
        ModelState.AddModelError("", AdminDisplay.Error(result.Error));
        return Page();
    }
}
