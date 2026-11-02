using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MosWord2019.Server.Security;

public sealed record SessionIdentity(Guid SessionId, Guid CredentialVersion);

public sealed class SessionTokens(IDataProtectionProvider provider)
{
    private readonly IDataProtector access = provider.CreateProtector("MosWord2019.Learner.Access.v1");
    private readonly IDataProtector resume = provider.CreateProtector("MosWord2019.Learner.Resume.v1");

    public string Issue(Guid sessionId, Guid credentialVersion, bool forResume = false) =>
        (forResume ? resume : access).Protect(JsonSerializer.Serialize(new SessionIdentity(sessionId, credentialVersion)));

    public SessionIdentity? Read(string? token, bool forResume = false)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return null;
        try { return JsonSerializer.Deserialize<SessionIdentity>((forResume ? resume : access).Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }

    public static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }
}
