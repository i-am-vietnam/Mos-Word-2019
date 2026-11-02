using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MosWord2019.Server.Data;
using MosWord2019.Server.Security;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class HeartbeatRetentionTests(PostgresFixture postgres)
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
        public TaskCompletionSource<DailyTimer> Scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DailyTimer(callback, state, dueTime);
            Scheduled.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class DailyTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime => dueTime;
        public void Fire() => callback(state);
        public bool Change(TimeSpan due, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DeletesOnlyHeartbeatsStrictlyOlderThanNinetyDaysAndIsIdempotent()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        var cutoff = clock.Now.AddDays(-90);
        var account = new Account { Username = "learner", NormalizedUsername = "LEARNER" };
        var session = new Session { AccountId = account.Id, LoginRequestId = Guid.NewGuid(),
            AccountCredentialVersion = account.CredentialVersion, MachineId = new string('a', 64),
            StartedAt = cutoff.AddDays(-1), LastHeartbeatAt = cutoff.AddDays(-1) };
        var old = new SessionHeartbeat { SessionId = session.Id, ReceivedAt = cutoff.AddMilliseconds(-1) };
        var boundary = new SessionHeartbeat { SessionId = session.Id, ReceivedAt = cutoff };
        var newer = new SessionHeartbeat { SessionId = session.Id, ReceivedAt = cutoff.AddMilliseconds(1) };
        var future = new SessionHeartbeat { SessionId = session.Id, ReceivedAt = clock.Now.AddDays(1) };
        await using (var db = PostgresFixture.Open(connection))
        {
            db.AddRange(account, session, old, boundary, newer, future,
                new SessionOfflineGap { SessionId = session.Id, StartedAt = cutoff.AddDays(-1), EndedAt = cutoff.AddHours(-23) },
                new Result { SessionId = session.Id, ReceivedAt = cutoff.AddDays(-1) },
                new AuditEntry { AccountId = account.Id, Action = "Retained", CreatedAt = cutoff.AddDays(-1) });
            await db.SaveChangesAsync();
            var retention = new HeartbeatRetention(db, clock);
            Assert.Equal(1, await retention.CleanAsync(default));
            Assert.Equal(0, await retention.CleanAsync(default));
        }
        await using var reopened = PostgresFixture.Open(connection);
        Assert.Equal(new[] { boundary.Id, newer.Id, future.Id },
            await reopened.Heartbeats.OrderBy(x => x.ReceivedAt).Select(x => x.Id).ToArrayAsync());
        Assert.Equal(session.LastHeartbeatAt, (await reopened.Sessions.SingleAsync()).LastHeartbeatAt);
        Assert.Single(await reopened.Accounts.ToListAsync());
        Assert.Single(await reopened.OfflineGaps.ToListAsync());
        Assert.Single(await reopened.Results.ToListAsync());
        Assert.Single(await reopened.AuditLog.ToListAsync());
    }

    [Fact]
    public async Task WorkerCatchesUpOnStartupRunsAgainAfterADayAndStopsWithoutWaiting()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var clock = new Clock();
        await using (var db = PostgresFixture.Open(connection))
        {
            var account = new Account { Username = "learner", NormalizedUsername = "LEARNER" };
            var session = new Session { AccountId = account.Id, LoginRequestId = Guid.NewGuid(),
                AccountCredentialVersion = account.CredentialVersion, MachineId = new string('a', 64),
                StartedAt = clock.Now.AddDays(-91) };
            db.AddRange(account, session,
                new SessionHeartbeat { SessionId = session.Id, ReceivedAt = clock.Now.AddDays(-91) });
            await db.SaveChangesAsync();
        }
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddDbContext<ServerDbContext>(options => options.UseNpgsql(connection));
        services.AddScoped<HeartbeatRetention>();
        await using var provider = services.BuildServiceProvider();
        using var worker = new HeartbeatRetentionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            clock, NullLogger<HeartbeatRetentionWorker>.Instance);
        await worker.StartAsync(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstDelay = await clock.Scheduled.Task.WaitAsync(timeout.Token);
        Assert.Equal(TimeSpan.FromHours(24), firstDelay.DueTime);
        await using var reopened = PostgresFixture.Open(connection);
        Assert.Empty(await reopened.Heartbeats.ToListAsync(timeout.Token));
        var persisted = await reopened.Sessions.SingleAsync(timeout.Token);
        reopened.Heartbeats.Add(new SessionHeartbeat { SessionId = persisted.Id, ReceivedAt = clock.Now.AddDays(-91) });
        await reopened.SaveChangesAsync(timeout.Token);
        clock.Scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Now = clock.Now.AddDays(1);
        firstDelay.Fire();
        var secondDelay = await clock.Scheduled.Task.WaitAsync(timeout.Token);
        Assert.Equal(TimeSpan.FromHours(24), secondDelay.DueTime);
        Assert.Empty(await reopened.Heartbeats.AsNoTracking().ToListAsync(timeout.Token));
        await worker.StopAsync(timeout.Token);
        Assert.False(worker.ExecuteTask!.IsFaulted);
        Assert.Single(await reopened.Sessions.ToListAsync());
    }
}
