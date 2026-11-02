namespace MosWord2019.Server.Security;

public sealed class LearnerLoginEndpoint;
public sealed class LearnerResumeEndpoint;

public static class LearnerEndpoints
{
    private static IResult Result(SessionOperation result) => Results.Json(result.Body, statusCode: result.Status);

    public static void MapLearnerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, LearnerSessions sessions,
            LoginThrottle throttle, CancellationToken ct) =>
        {
            string normalized;
            try { normalized = AdminAccess.NormalizeUsername(request.Username ?? ""); }
            catch (ArgumentException) { normalized = "invalid"; }
            if (!await throttle.AllowAsync("user:" + normalized, ct)) return Results.StatusCode(429);
            return Result(await sessions.LoginAsync(request, ct));
        }).WithMetadata(new LearnerLoginEndpoint());
        app.MapPost("/api/sessions/mode", async (ModeRequest request, HttpContext context,
            LearnerSessions sessions, CancellationToken ct) =>
                Result(await sessions.ModeAsync(SessionTokens.Bearer(context), request, ct)));
        app.MapPost("/api/sessions/{id:guid}/end", async (Guid id, HttpContext context,
            LearnerSessions sessions, CancellationToken ct) =>
                Result(await sessions.EndAsync(SessionTokens.Bearer(context), id, ct)));
        app.MapPost("/api/sessions/resume", async (ResumeRequest request, SessionTokens tokens,
            LoginThrottle throttle, LearnerSessions sessions, CancellationToken ct) =>
        {
            var identity = tokens.Read(request.ResumeToken, true);
            if (identity != null && !await throttle.AllowAsync("session:" + identity.SessionId, ct))
                return Results.StatusCode(429);
            return Result(await sessions.ResumeAsync(request, ct));
        }).WithMetadata(new LearnerResumeEndpoint());
    }
}
