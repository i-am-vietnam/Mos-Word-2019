using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

var command = args.FirstOrDefault();
var isCommand = command is "migrate" or "create-admin";
var builder = WebApplication.CreateBuilder(isCommand ? [] : args);
builder.Services.AddDbContext<ServerDbContext>(options =>
{
    var connection = builder.Configuration.GetConnectionString("Database");
    if (string.IsNullOrWhiteSpace(connection))
        throw new InvalidOperationException("Set ConnectionStrings__Database through the environment.");
    options.UseNpgsql(connection);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.AddSingleton<AdminAccess>();
var protection = builder.Services.AddDataProtection().SetApplicationName("MosWord2019.Server");
if (builder.Configuration["DataProtection:KeyPath"] is { Length: > 0 } keyPath)
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Admin/Login";
    options.AccessDeniedPath = "/Admin/Login";
    options.Cookie.Name = "__Host-MosWordAdmin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = async context =>
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var db = context.HttpContext.RequestServices.GetRequiredService<ServerDbContext>();
        var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var account = Guid.TryParse(id, out var accountId) ? await db.Accounts.FindAsync(accountId) : null;
        if (account == null || !AdminAccess.IsAllowed(account, time.GetUtcNow())) context.RejectPrincipal();
    };
});
builder.Services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("Admin")));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-MosWordAntiforgery";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/Admin", "Admin")
    .AllowAnonymousToPage("/Admin/Login"));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!context.Request.Path.StartsWithSegments("/Admin"))
            return RateLimitPartition.GetNoLimiter("public");
        var login = context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath == "/Admin/Login" &&
            HttpMethods.IsPost(context.Request.Method);
        var key = (login ? "login:" : "admin:") + context.Connection.RemoteIpAddress;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = login ? 5 : 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        });
    });
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
var app = builder.Build();

if (isCommand)
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        if (command == "migrate" && args.Length == 1) await db.Database.MigrateAsync();
        else if (command == "create-admin" && args.Length == 2)
        {
            var password = Environment.GetEnvironmentVariable("MOSWORD_ADMIN_PASSWORD") ?? "";
            await AdminBootstrap.CreateFirstAsync(db,
                scope.ServiceProvider.GetRequiredService<IPasswordHasher<Account>>(), args[1], password);
        }
        else throw new ArgumentException("Usage: migrate | create-admin <username> (MOSWORD_ADMIN_PASSWORD environment variable).");
        Console.WriteLine("Command completed successfully.");
    }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
    catch (InvalidOperationException)
    { Console.Error.WriteLine("Command refused. Check configuration, credentials policy and whether an admin already exists."); Environment.ExitCode = 1; }
    catch (Exception)
    { Console.Error.WriteLine("Database command failed. Check database availability and apply migrations before bootstrap."); Environment.ExitCode = 1; }
    finally { await app.DisposeAsync(); }
    return;
}

if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapRazorPages();
app.Run();

public partial class Program { }
