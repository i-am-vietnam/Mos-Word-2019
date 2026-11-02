using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MosWord2019.Server.Data;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken) ||
                (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return HealthCheckResult.Unhealthy("Database is not ready.");
            return HealthCheckResult.Healthy();
        }
        catch (Exception)
        {
            // Never include connection details in the health response.
            return HealthCheckResult.Unhealthy("Database is not ready.");
        }
    }
}
