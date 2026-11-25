using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SaturdayScout.Infrastructure.Hosting;
using Xunit;

namespace SaturdayScout.Player.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task LivenessIsAnonymousAndDoesNotCheckDatabase()
    {
        var probe = new StubProbe((_) => throw new InvalidOperationException("Must not run"));
        using var factory = CreateFactory(probe);
        using var client = Client(factory);

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, probe.Calls);
        Assert.Equal("no-store, no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ReadinessReturnsMinimalAnonymousSuccessWhenDatabaseProbeSucceeds()
    {
        var probe = new StubProbe(_ => Task.FromResult(true));
        using var factory = CreateFactory(probe);
        using var client = Client(factory);

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, probe.Calls);
        Assert.Equal("no-store, no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ReadinessReturns503WithoutDiagnosticDetailsWhenDatabaseIsUnavailable()
    {
        var probe = new StubProbe(_ => throw new InvalidOperationException("Host=db.internal;Password=do-not-return"));
        using var factory = CreateFactory(probe);
        using var client = Client(factory);

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Unavailable", body);
        Assert.DoesNotContain("database", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadinessHasBoundedTimeoutAndHonorsRequestCancellationToken()
    {
        var probe = new StubProbe(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        });
        using var factory = CreateFactory(probe, timeoutSeconds: 1);
        using var client = Client(factory);
        var started = DateTime.UtcNow;

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReadinessStopsWaitingWhenClientDisconnectsEvenIfProbeDoesNotCooperate()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new StubProbe(_ =>
        {
            started.SetResult();
            return neverCompletes.Task;
        });
        using var factory = CreateFactory(probe);
        using var client = Client(factory);
        using var disconnect = new CancellationTokenSource();

        var request = client.GetAsync("/health/ready", disconnect.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        disconnect.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task HeadIsSupportedAndPlayerAuthenticationRoutesRemainProtected()
    {
        using var factory = CreateFactory(new StubProbe(_ => Task.FromResult(true)));
        using var client = Client(factory);

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health/live"));
        using var protectedResponse = await client.GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task InvalidReadinessTimeoutFailsConfigurationAtStartup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            ConfigureBase(builder);
            builder.UseSetting(ScoutHealthOptions.TimeoutSetting, "0");
        });

        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/health/live");
        });
    }

    private static WebApplicationFactory<Program> CreateFactory(StubProbe probe, int timeoutSeconds = 3) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            ConfigureBase(builder);
            builder.UseSetting(ScoutHealthOptions.TimeoutSetting, timeoutSeconds.ToString());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDatabaseReadinessProbe>();
                services.AddSingleton<IDatabaseReadinessProbe>(probe);
            });
        });

    private static void ConfigureBase(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1");
        builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", Path.Combine(Path.GetTempPath(), "saturday-scout-health-test-keys"));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private sealed class StubProbe(Func<CancellationToken, Task<bool>> check) : IDatabaseReadinessProbe
    {
        public int Calls { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return check(cancellationToken);
        }
    }
}
