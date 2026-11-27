using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SaturdayScout.Infrastructure.Hosting;
using Xunit;

namespace SaturdayScout.Admin.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task HealthEndpointsAreAnonymousAndAdminMfaRouteRemainsProtected()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", Path.Combine(Path.GetTempPath(), $"scout-admin-health-{Guid.NewGuid():N}"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDatabaseReadinessProbe>();
                services.AddSingleton<IDatabaseReadinessProbe>(new HealthyProbe());
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var protectedSession = await client.GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, protectedSession.StatusCode);
        Assert.Null(protectedSession.Headers.Location);
    }

    private sealed class HealthyProbe : IDatabaseReadinessProbe
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
