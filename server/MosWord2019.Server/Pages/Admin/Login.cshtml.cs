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
    [BindProperty, Required(ErrorMessage = "Vui lòng nhập tài khoản."), StringLength(64, ErrorMessage = "Tài khoản tối đa 64 ký tự.")] public string Username { get; set; } = "";
    [BindProperty, Required(ErrorMessage = "Vui lòng nhập mật khẩu."), StringLength(128, ErrorMessage = "Mật khẩu tối đa 128 ký tự."), DataType(DataType.Password)] public string Password { get; set; } = "";

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) { Password = ""; ModelState.Remove(nameof(Password)); ModelState.AddModelError("", "Vui lòng nhập tài khoản và mật khẩu hợp lệ."); return Page(); }
        var account = await access.AuthenticateAsync(db, Username, Password, time.GetUtcNow(), cancellationToken);
        Password = "";
        ModelState.Remove(nameof(Password));
        if (account == null)
        {
            ModelState.AddModelError("", "Thông tin quản trị không hợp lệ. Vui lòng thử lại sau.");
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
