using System.Threading.RateLimiting;

namespace MosWord2019.Server.Security;

public sealed class LoginThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        { PermitLimit = key.StartsWith("user:", StringComparison.Ordinal) ? 10 : 60,
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public async Task<bool> AllowAsync(string key, CancellationToken cancellationToken)
    {
        using var lease = await limiter.AcquireAsync(key, 1, cancellationToken);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
