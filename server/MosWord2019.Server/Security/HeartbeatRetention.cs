using Microsoft.EntityFrameworkCore;
using MosWord2019.Server.Data;

namespace MosWord2019.Server.Security;

public sealed class HeartbeatRetention(ServerDbContext db, TimeProvider time)
{
    public Task<int> CleanAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-90);
        return db.Heartbeats.Where(x => x.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}

public sealed class HeartbeatRetentionWorker(IServiceScopeFactory scopes, TimeProvider time,
    ILogger<HeartbeatRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<HeartbeatRetention>().CleanAsync(stoppingToken);
                logger.LogInformation("Heartbeat retention removed {Count} expired history rows.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Heartbeat retention failed; the next daily run will retry."); }

            try { await Task.Delay(TimeSpan.FromHours(24), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
