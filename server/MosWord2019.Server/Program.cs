using System.Security.Claims;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;
using MosWord2019.Server.Packages;
using MosWord2019.Server.ResultHandling;

var command = args.FirstOrDefault();
var isCommand = command is "migrate" or "create-admin" or "import-packages" or "package-public-key";
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
builder.Services.AddSingleton<SessionTokens>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<LearnerSessions>();
builder.Services.AddScoped<SessionHeartbeats>();
builder.Services.AddScoped<HeartbeatRetention>();
builder.Services.AddHostedService<HeartbeatRetentionWorker>();
builder.Services.AddScoped<ResultUploads>();
builder.Services.AddScoped<IServerExamPool, ServerExamPool>();
builder.Services.AddSingleton<PackageStorage>();
builder.Services.AddScoped<PackageImporter>();
builder.Services.AddScoped<AccountAdministration>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddDataProtection().SetApplicationName("MosWord2019.Server");
KeyStorage.Configure(builder);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
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
        if (account == null || !AdminAccess.IsAllowed(account, time.GetUtcNow(), newLogin: false) ||
            context.Principal?.FindFirstValue("credential_version") != account.CredentialVersion.ToString()) context.RejectPrincipal();
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
    .AllowAnonymousToPage("/Admin/Login")).AddMvcOptions(options =>
{
    var messages = options.ModelBindingMessageProvider;
    messages.SetAttemptedValueIsInvalidAccessor((_, _) => "Giá trị không hợp lệ. Vui lòng kiểm tra lại.");
    messages.SetValueMustBeANumberAccessor(_ => "Vui lòng nhập một số hợp lệ.");
    messages.SetValueMustNotBeNullAccessor(_ => "Vui lòng nhập giá trị.");
    messages.SetValueIsInvalidAccessor(_ => "Giá trị không hợp lệ.");
    messages.SetUnknownValueIsInvalidAccessor(_ => "Giá trị không hợp lệ.");
    messages.SetMissingBindRequiredValueAccessor(_ => "Vui lòng nhập giá trị bắt buộc.");
    messages.SetMissingKeyOrValueAccessor(() => "Thiếu thông tin bắt buộc.");
    messages.SetMissingRequestBodyRequiredValueAccessor(() => "Thiếu nội dung yêu cầu.");
    messages.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => "Giá trị không hợp lệ.");
    messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => "Giá trị không hợp lệ.");
    messages.SetNonPropertyValueMustBeANumberAccessor(() => "Vui lòng nhập một số hợp lệ.");
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var ip = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        if (context.GetEndpoint()?.Metadata.GetMetadata<LearnerLoginEndpoint>() != null)
            return RateLimitPartition.GetFixedWindowLimiter("learner-ip:" + ip, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            var isResume = context.GetEndpoint()?.Metadata.GetMetadata<LearnerResumeEndpoint>() != null;
            var tokenService = context.RequestServices.GetRequiredService<SessionTokens>();
            var identity = isResume ? null : tokenService.Read(SessionTokens.Bearer(context));
            if (context.GetEndpoint()?.Metadata.GetMetadata<ResultUploadEndpoint>() != null)
                identity ??= tokenService.ReadUpload(SessionTokens.Bearer(context));
            if (identity != null) return RateLimitPartition.GetNoLimiter("authenticated-session");
            return RateLimitPartition.GetFixedWindowLimiter("api-ip:" + ip, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }
        if (!context.Request.Path.StartsWithSegments("/Admin"))
            return RateLimitPartition.GetNoLimiter("public");
        var login = context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath == "/Admin/Login" &&
            HttpMethods.IsPost(context.Request.Method);
        var key = (login ? "login:" : "admin:") + ip;
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = login ? 5 : 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        });
    });
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
var app = builder.Build();
KeyStorage.Validate(app.Configuration, app.Environment);

if (isCommand)
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        if (command == "migrate" && args.Length == 1) await db.Database.MigrateAsync();
        else if (command == "import-packages" && args.Length == 2)
            Console.WriteLine("Imported packages: " + await scope.ServiceProvider.GetRequiredService<PackageImporter>().ImportAsync(args[1]));
        else if (command == "package-public-key" && args.Length == 1)
            Console.WriteLine(Convert.ToBase64String(scope.ServiceProvider.GetRequiredService<PackageStorage>().GetPublicKey()));
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

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next(context);
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/Admin"))
        context.Response.Headers.CacheControl = "no-store";
    // Access and resume requests share the same singleton budget for the underlying session.
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.GetEndpoint()?.Metadata.GetMetadata<LearnerLoginEndpoint>() == null &&
        context.GetEndpoint()?.Metadata.GetMetadata<LearnerResumeEndpoint>() == null)
    {
        var identity = context.RequestServices.GetRequiredService<SessionTokens>().Read(SessionTokens.Bearer(context));
        if (context.GetEndpoint()?.Metadata.GetMetadata<ResultUploadEndpoint>() != null)
            identity ??= context.RequestServices.GetRequiredService<SessionTokens>().ReadUpload(SessionTokens.Bearer(context));
        if (identity != null && !await context.RequestServices.GetRequiredService<LoginThrottle>()
            .AllowAsync("session:" + identity.SessionId, context.RequestAborted))
        { context.Response.StatusCode = 429; return; }
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapRazorPages();
app.MapLearnerEndpoints();
app.MapPackageEndpoints();
ResultUploads.Map(app);
app.Run();

public partial class Program { }
