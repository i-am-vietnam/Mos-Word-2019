using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MosWord2019.Server.Security;

public sealed record SessionIdentity(Guid SessionId, Guid CredentialVersion);

public sealed class SessionTokens(IDataProtectionProvider provider, TimeProvider? timeProvider = null)
{
    private sealed record Envelope(SessionIdentity Identity, DateTimeOffset ExpiresAt);
    private sealed record ObservedToken(SessionIdentity Identity, string TimedToken);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly ITimeLimitedDataProtector access = provider.CreateProtector("MosWord2019.Learner.Access.v2").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector resume = provider.CreateProtector("MosWord2019.Learner.Resume.v2").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector upload = provider.CreateProtector("MosWord2019.Learner.Upload.v1").ToTimeLimitedDataProtector();
    private readonly IDataProtector accessObservation = provider.CreateProtector("MosWord2019.Learner.AccessObservation.v1");
    private readonly IDataProtector resumeObservation = provider.CreateProtector("MosWord2019.Learner.ResumeObservation.v1");

    public string Issue(Guid sessionId, Guid credentialVersion, bool forResume = false) =>
        (forResume ? resumeObservation : accessObservation).Protect(JsonSerializer.Serialize(
            new ObservedToken(new SessionIdentity(sessionId, credentialVersion),
                IssueCore(forResume ? resume : access, sessionId, credentialVersion,
                    forResume ? TimeSpan.FromMinutes(255) : TimeSpan.FromMinutes(30)))));

    public string IssueUpload(Guid sessionId, Guid credentialVersion) =>
        IssueCore(upload, sessionId, credentialVersion, TimeSpan.FromMinutes(1575));

    private string IssueCore(ITimeLimitedDataProtector protector, Guid sessionId, Guid credentialVersion, TimeSpan lifetime) =>
        protector.Protect(JsonSerializer.Serialize(new Envelope(new SessionIdentity(sessionId, credentialVersion),
            time.GetUtcNow().Add(lifetime))), lifetime);

    public SessionIdentity? Read(string? token, bool forResume = false)
    {
        var observed = ReadObserved(token, forResume);
        var identity = ReadCore(forResume ? resume : access, observed?.TimedToken);
        return identity == observed?.Identity ? identity : null;
    }

    // This authenticated hint is only for observing expiration, never for granting permission.
    public SessionIdentity? ReadExpirationHint(string? token, bool forResume = false) =>
        ReadObserved(token, forResume)?.Identity;

    private ObservedToken? ReadObserved(string? token, bool forResume)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return null;
        try { return JsonSerializer.Deserialize<ObservedToken>((forResume ? resumeObservation : accessObservation).Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }
    public SessionIdentity? ReadUpload(string? token) => ReadCore(upload, token);

    private SessionIdentity? ReadCore(ITimeLimitedDataProtector protector, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(protector.Unprotect(token, out _));
            return envelope != null && time.GetUtcNow() < envelope.ExpiresAt ? envelope.Identity : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return null; }
    }

    public static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }
}
