using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MosWord2019.Server.Packages;

public sealed record DefinitionReport(string? DefinitionHash);

public static class PackageEndpoints
{
    private static readonly JsonSerializerSettings Json = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    public static void MapPackageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/packages/manifest", ManifestAsync);
        app.MapGet("/api/packages/{id:guid}/download", DownloadAsync);
        app.MapPost("/api/sessions/definition", ReportAsync);
    }

    private static void Refresh(HttpContext context, SessionTokens tokens, Session session, Account account) =>
        context.Response.Headers["Session-Token"] = tokens.Issue(session.Id, account.CredentialVersion);

    private static async Task<IResult> ManifestAsync(HttpContext context, ServerDbContext db, LearnerSessions sessions,
        SessionTokens tokens, PackageStorage storage, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await sessions.FindAsync(SessionTokens.Bearer(context), false, ct);
        if (found == null) { await transaction.CommitAsync(ct); return Results.Unauthorized(); }
        var (session, account) = found.Value;
        if (session.Mode == null) return Results.Conflict(new { error = "mode_required" });
        List<Package> packages;
        ExamSnapshot? snapshot = null;
        if (session.Mode == "Testing")
        {
            snapshot = JsonConvert.DeserializeObject<ExamSnapshot>(session.ExamSnapshotJson ?? "null");
            if (snapshot == null || snapshot.PackageIds.Count != 7) return Results.StatusCode(503);
            packages = await db.Packages.AsNoTracking().Where(p => snapshot.PackageIds.Contains(p.Id)).ToListAsync(ct);
            if (packages.Count != 7) return Results.StatusCode(503);
        }
        else packages = await ServerExamPool.LatestAsync(db, ct);
        if (packages.Count == 0) return Results.StatusCode(503);
        try
        {
            var descriptors = packages.OrderBy(p => p.Code, StringComparer.Ordinal).Select(p => new
            {
                p.Id, p.Code, p.Version, p.Sha256, p.ZipSha256, p.SizeBytes,
                key = Convert.ToBase64String(storage.ReadKey(p)), downloadPath = "/api/packages/" + p.Id + "/download"
            }).ToArray();
            byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                schemaVersion = 1, sessionId = session.Id, mode = session.Mode,
                examSeed = session.ExamSeed?.ToString(CultureInfo.InvariantCulture), definitionHash = session.ExamDefinitionHash,
                catalog = snapshot?.Catalog, examPool = snapshot?.ExamPool, packages = descriptors
            }, Json));
            var envelope = new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(storage.Sign(payload)) };
            Refresh(context, tokens, session, account);
            await transaction.CommitAsync(ct);
            return Results.Json(envelope);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { return Results.StatusCode(503); }
    }

    private static async Task<IResult> ReportAsync(DefinitionReport report, HttpContext context, ServerDbContext db,
        LearnerSessions sessions, SessionTokens tokens, TimeProvider time, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await sessions.FindAsync(SessionTokens.Bearer(context), false, ct);
        if (found == null) { await transaction.CommitAsync(ct); return Results.Unauthorized(); }
        var (session, account) = found.Value;
        if (session.Mode != "Testing" || report.DefinitionHash != session.ExamDefinitionHash)
            return Results.Conflict(new { error = "definition_mismatch" });
        if (session.ReportedDefinitionHash == null)
        {
            session.ReportedDefinitionHash = report.DefinitionHash;
            db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "ExamDefinitionConfirmed", CreatedAt = time.GetUtcNow(),
                DetailsJson = JsonConvert.SerializeObject(new { sessionId = session.Id, definitionHash = report.DefinitionHash }) });
            await db.SaveChangesAsync(ct);
        }
        Refresh(context, tokens, session, account);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { sessionId = session.Id, serverTime = time.GetUtcNow() });
    }

    private static async Task<IResult> DownloadAsync(Guid id, HttpContext context, ServerDbContext db, LearnerSessions sessions,
        SessionTokens tokens, PackageStorage storage, TimeProvider time, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await sessions.FindAsync(SessionTokens.Bearer(context), false, ct);
        if (found == null) { await transaction.CommitAsync(ct); return Results.Unauthorized(); }
        var (session, account) = found.Value;
        if (session.Mode == null) return Results.Conflict(new { error = "mode_required" });
        if (session.Mode == "Testing")
        {
            var snapshot = JsonConvert.DeserializeObject<ExamSnapshot>(session.ExamSnapshotJson ?? "null");
            if (snapshot == null || !snapshot.PackageIds.Contains(id)) return Results.StatusCode(403);
            if (session.ReportedDefinitionHash != session.ExamDefinitionHash)
                return Results.Conflict(new { error = "definition_report_required" });
        }
        var package = await db.Packages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (package == null) return Results.NotFound();
        byte[] blob;
        try { blob = storage.ReadBlob(package); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { return Results.StatusCode(503); }
        db.AuditLog.Add(new AuditEntry { AccountId = account.Id, Action = "PackageDownloaded", CreatedAt = time.GetUtcNow(),
            DetailsJson = JsonConvert.SerializeObject(new { sessionId = session.Id, packageId = id, package.Code, package.Version, package.SizeBytes }) });
        await db.SaveChangesAsync(ct);
        Refresh(context, tokens, session, account);
        await transaction.CommitAsync(ct);
        return Results.File(blob, "application/octet-stream", enableRangeProcessing: false);
    }
}
