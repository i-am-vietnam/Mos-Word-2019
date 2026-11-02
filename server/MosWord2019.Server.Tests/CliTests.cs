using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace MosWord2019.Server.Tests;

[Collection("PostgreSQL")]
public sealed class CliTests(PostgresFixture postgres)
{
    private static async Task<(int ExitCode, string Output)> RunAsync(string connection, string password, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment["ConnectionStrings__Database"] = connection;
        start.Environment["MOSWORD_ADMIN_PASSWORD"] = password;
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }

    [Fact]
    public async Task CliMigratesBootstrapsAndRefusesRepeatedBootstrapWithoutLeakingPassword()
    {
        var connection = await postgres.CreateDatabaseAsync(migrate: false);
        var password = Guid.NewGuid().ToString("N");
        var migrated = await RunAsync(connection, "", "migrate");
        Assert.Equal(0, migrated.ExitCode);
        var created = await RunAsync(connection, password, "create-admin", "owner");
        Assert.Equal(0, created.ExitCode);
        Assert.DoesNotContain(password, created.Output);
        var repeated = await RunAsync(connection, password, "create-admin", "other");
        Assert.Equal(1, repeated.ExitCode);
        Assert.DoesNotContain(password, repeated.Output);
        await using var db = PostgresFixture.Open(connection);
        Assert.Single(await db.Accounts.ToListAsync());
        Assert.Single(await db.AuditLog.ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task MissingOrWeakBootstrapPasswordReturnsNonzeroWithoutWritingAccount(string password)
    {
        var connection = await postgres.CreateDatabaseAsync();
        var result = await RunAsync(connection, password, "create-admin", "owner");
        Assert.Equal(1, result.ExitCode);
        await using var db = PostgresFixture.Open(connection);
        Assert.Empty(await db.Accounts.ToListAsync());
    }
}
