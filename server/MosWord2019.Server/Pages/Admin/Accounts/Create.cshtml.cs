using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Pages.Admin.Accounts;

public sealed class CreateModel(AccountAdministration administration) : PageModel
{
    [BindProperty, Required, StringLength(64)] public string Username { get; set; } = "";
    [BindProperty, Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty, Range(0, int.MaxValue)] public int MaxUses { get; set; }
    [BindProperty] public DateTime? ExpiresUtc { get; set; }
    [BindProperty] public bool Locked { get; set; }

    public void OnGet() { }
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var password = Password;
        Password = "";
        ModelState.Remove(nameof(Password));
        if (!AccountAdministration.ValidPassword(password))
            ModelState.AddModelError(nameof(Password), "Password must contain 12-128 characters and cannot be blank.");
        if (!ModelState.IsValid) return Page();
        var expiry = ExpiresUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(ExpiresUtc.Value, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        var result = await administration.CreateAsync(Username, password, MaxUses, expiry, Locked, cancellationToken);
        if (result.Success) return RedirectToPage("Edit", new { id = result.AccountId });
        ModelState.AddModelError("", result.Error!);
        return Page();
    }
}
