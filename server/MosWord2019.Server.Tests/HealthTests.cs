using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MosWord2019.Server.Tests;

public sealed class HealthTests
{
    [Fact]
    public async Task HealthIsAvailableWithoutDatabaseOrAuthentication()
    {
        await using var factory = new ServerFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnavailableDatabaseReturnsGenericReadinessFailure()
    {
        await using var factory = new ServerFactory("Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1");
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
    }
}
