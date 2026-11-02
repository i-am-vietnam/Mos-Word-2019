using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Pages.Admin;

public sealed class LoginModel(ServerDbContext db, AdminAccess access, TimeProvider time) : PageModel
{
    [BindProperty, Required, StringLength(64)] public string Username { get; set; } = "";
    [BindProperty, Required, StringLength(128), DataType(DataType.Password)] public string Password { get; set; } = "";

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();
        var account = await access.AuthenticateAsync(db, Username, Password, time.GetUtcNow(), cancellationToken);
        Password = "";
        ModelState.Remove(nameof(Password));
        if (account == null)
        {
            ModelState.AddModelError("", "Invalid administrator credentials. Please try again later.");
            return Page();
        }
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim("credential_version", account.CredentialVersion.ToString()),
            new Claim(ClaimTypes.Name, account.Username), new Claim(ClaimTypes.Role, "Admin")
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return RedirectToPage("/Admin/Index");
    }
}
