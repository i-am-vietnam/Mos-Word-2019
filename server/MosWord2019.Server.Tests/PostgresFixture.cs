using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MosWord2019.Server.Data;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MosWord2019.Server.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).Build();

    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    public async Task<string> CreateDatabaseAsync(bool migrate = true)
    {
        var name = "c0_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await command.ExecuteNonQueryAsync();
        var settings = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = name, Pooling = false };
        if (migrate)
        {
            await using var db = Open(settings.ConnectionString);
            await db.Database.MigrateAsync();
        }
        return settings.ConnectionString;
    }

    public static ServerDbContext Open(string connection) => new(
        new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(connection).Options);
}

[CollectionDefinition("PostgreSQL")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }

public sealed class ServerFactory(string connection, Dictionary<string, string?>? settings = null,
    TimeProvider? clock = null, bool realExamPool = false) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>(settings ?? []) { ["ConnectionStrings:Database"] = connection,
                ["DataProtection:KeyPath"] = Path.Combine(Path.GetTempPath(), "MosWord2019-test-keys") };
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            if (clock != null) services.AddSingleton(clock);
            if (!realExamPool) services.AddScoped<MosWord2019.Server.Packages.IServerExamPool, FixtureExamPool>();
        });
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
}

internal sealed class FixtureExamPool : MosWord2019.Server.Packages.IServerExamPool
{
    public Task<MosWord2019.Server.Packages.ServerExam> CreateAsync(CancellationToken ct) => Task.FromResult(
        new MosWord2019.Server.Packages.ServerExam(123, new string('a', 64), "{\"PackageIds\":[],\"Catalog\":[],\"ExamPool\":[]}"));
}
