using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using SaturdayScout.Infrastructure.Hosting;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Player.Tests;

public sealed class PlayerAuthRateLimitingTests
{
    private const string LoginPath = "/auth/login";

    [Fact]
    public async Task RepeatedLoginAttemptsFromOneIpReturnMinimal429()
    {
        using var factory = CreateFactory(permitLimit: 3);
        using var client = PeerClient(factory, IPAddress.Parse("198.51.100.20"));

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(HttpStatusCode.BadRequest, (await PostLoginAsync(client)).StatusCode);
        using var rejected = await PostLoginAsync(client, email: "known-player@example.test");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        Assert.Equal("text/plain", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Null(rejected.Headers.Location);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(PlayerAuthRateLimiting.RejectionMessage, body);
        Assert.DoesNotContain("known-player", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Not Found", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectClientIpsHaveSeparatePartitions()
    {
        using var factory = CreateFactory(permitLimit: 2);
        using var first = PeerClient(factory, IPAddress.Parse("198.51.100.21"));
        using var second = PeerClient(factory, IPAddress.Parse("198.51.100.22"));

        await ExhaustAsync(first, 2);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostLoginAsync(second)).StatusCode);
    }

    [Fact]
    public async Task Ipv6ClientsArePartitionedByTheirSlash64Prefix()
    {
        using var factory = CreateFactory(permitLimit: 2);
        using var first = PeerClient(factory, IPAddress.Parse("2001:db8:1:2::10"));
        using var samePrefix = PeerClient(factory, IPAddress.Parse("2001:db8:1:2:ffff::99"));
        using var otherPrefix = PeerClient(factory, IPAddress.Parse("2001:db8:1:3::10"));

        await ExhaustAsync(first, 2);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(samePrefix)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostLoginAsync(otherPrefix)).StatusCode);
    }

    [Theory]
    [InlineData("198.51.100.7", "198.51.100.7")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("2001:db8:aa:bb:1:2:3:4", "2001:db8:aa:bb::/64")]
    public void PartitionKeyNormalizesAddresses(string address, string expected) =>
        Assert.Equal(expected, PlayerAuthRateLimiting.PartitionKey(IPAddress.Parse(address)));

    [Fact]
    public void PartitionCapUsesBoundedOverflowBucketsForNewClients()
    {
        using var partitions = CreatePartitions(maximumTrackedClients: 2, overflowPartitions: 8);
        var firstAddress = IPAddress.Parse("198.51.100.1");
        var secondAddress = IPAddress.Parse("198.51.100.2");
        var first = partitions.GetPartitionKey(firstAddress);
        var second = partitions.GetPartitionKey(secondAddress);
        Assert.NotEqual(first, second);

        var overflow = Enumerable.Range(3, 200)
            .Select(lastOctet => partitions.GetPartitionKey(IPAddress.Parse($"198.51.100.{lastOctet}")))
            .ToArray();

        Assert.All(overflow, key => Assert.StartsWith("overflow-", key));
        Assert.InRange(overflow.Distinct().Count(), 2, 8);
        Assert.Equal(overflow[0], partitions.GetPartitionKey(IPAddress.Parse("198.51.100.3")));
    }

    [Fact]
    public void InactiveClientPartitionsAreReclaimedWithoutEvictingActiveClients()
    {
        var clock = new ManualTimeProvider();
        using var partitions = new PlayerAuthRateLimitPartitionKeys(2, 8, TimeSpan.FromMinutes(5), clock);
        var activeAddress = IPAddress.Parse("198.51.100.11");
        var inactiveAddress = IPAddress.Parse("198.51.100.12");
        var arrivingAddress = IPAddress.Parse("198.51.100.13");
        var activeKey = partitions.GetPartitionKey(activeAddress);
        var inactiveKey = partitions.GetPartitionKey(inactiveAddress);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(activeKey, partitions.GetPartitionKey(activeAddress));
        clock.Advance(TimeSpan.FromMinutes(2));

        var arrivingKey = partitions.GetPartitionKey(arrivingAddress);

        Assert.NotEqual(activeKey, arrivingKey);
        Assert.StartsWith("client-", arrivingKey);
        Assert.Equal(activeKey, partitions.GetPartitionKey(activeAddress));
        Assert.StartsWith("overflow-", partitions.GetPartitionKey(inactiveAddress));
        Assert.NotEqual(inactiveKey, arrivingKey);
    }

    [Fact]
    public void ConcurrentRequestsNearPartitionCapRemainBoundedAndStablePerClient()
    {
        const int capacity = 64;
        using var partitions = CreatePartitions(capacity, overflowPartitions: 16);
        var addresses = Enumerable.Range(1, 128)
            .Select(value => IPAddress.Parse($"198.51.{value / 256}.{value % 256}"))
            .ToArray();
        var results = new ConcurrentBag<(IPAddress Address, string Key)>();

        Parallel.For(0, 4096, index =>
        {
            var address = addresses[index % addresses.Length];
            results.Add((address, partitions.GetPartitionKey(address)));
        });

        foreach (var group in results.GroupBy(result => result.Address))
            Assert.Single(group.Select(result => result.Key).Distinct());

        var keys = results.Select(result => result.Key).Distinct().ToArray();
        Assert.Equal(capacity, keys.Count(key => key.StartsWith("client-", StringComparison.Ordinal)));
        Assert.InRange(keys.Count(key => key.StartsWith("overflow-", StringComparison.Ordinal)), 1, 16);
    }

    [Fact]
    public async Task TrustedProxyForwardedClientIpsHaveSeparatePartitions()
    {
        using var factory = CreateFactory(permitLimit: 2, trustedProxy: "127.0.0.1");
        using var proxy = PeerClient(factory, IPAddress.Loopback);

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(HttpStatusCode.BadRequest, (await PostLoginAsync(proxy, forwardedFor: "198.51.100.30")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await PostLoginAsync(proxy, forwardedFor: "198.51.100.30")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PostLoginAsync(proxy, forwardedFor: "198.51.100.31")).StatusCode);
    }

    [Fact]
    public async Task UntrustedPeerCannotBypassLimitWithRotatingForwardedFor()
    {
        using var factory = CreateFactory(permitLimit: 2, trustedProxy: "192.0.2.200");
        using var attacker = PeerClient(factory, IPAddress.Parse("203.0.113.40"));

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(HttpStatusCode.BadRequest,
                (await PostLoginAsync(attacker, forwardedFor: $"198.51.100.{attempt + 1}")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await PostLoginAsync(attacker, forwardedFor: "198.51.100.99")).StatusCode);
    }

    [Fact]
    public async Task ForwardedForIsIgnoredWhenForwardingIsDisabled()
    {
        using var factory = CreateFactory(permitLimit: 2);
        using var attacker = PeerClient(factory, IPAddress.Parse("203.0.113.41"));

        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(HttpStatusCode.BadRequest,
                (await PostLoginAsync(attacker, forwardedFor: $"198.51.100.{attempt + 50}")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await PostLoginAsync(attacker, forwardedFor: "198.51.100.60")).StatusCode);
    }

    [Fact]
    public async Task RejectedRequestsDoNotReachAuthenticationAntiforgeryOrTheLoginHandler()
    {
        var counters = new Counters();
        using var factory = CreateFactory(permitLimit: 2, configureServices: services =>
        {
            services.AddSingleton(counters);
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, CountingAuthenticationHandler>(
                CountingAuthenticationHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
                options.DefaultAuthenticateScheme = CountingAuthenticationHandler.SchemeName);
            services.RemoveAll<IAntiforgery>();
            services.AddSingleton<IAntiforgery>(new CountingAntiforgery(counters));
        });
        using var client = PeerClient(factory, IPAddress.Parse("198.51.100.40"));

        await ExhaustAsync(client, 2);
        var authenticationsBeforeRejection = counters.Authentications;
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(client)).StatusCode);

        Assert.Equal(2, counters.AntiforgeryValidations);
        Assert.Equal(authenticationsBeforeRejection, counters.Authentications);
    }

    [Fact]
    public async Task ExhaustedLoginLimitDoesNotThrottleHealthStaticOrOrdinaryPlayerRoutes()
    {
        using var factory = CreateFactory(permitLimit: 1, configureServices: services =>
        {
            services.RemoveAll<IDatabaseReadinessProbe>();
            services.AddSingleton<IDatabaseReadinessProbe>(new ReadyProbe());
        });
        using var client = PeerClient(factory, IPAddress.Parse("198.51.100.50"));
        await ExhaustAsync(client, 1);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLoginAsync(client)).StatusCode);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auth/antiforgery")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/login")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.css")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/react/")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/react/manifest.webmanifest")).StatusCode);
        }
    }

    [Fact]
    public async Task ProtectedSessionBffAndLogoutRoutesKeepExistingBehaviorAndAreNotLoginLimited()
    {
        using var factory = CreateFactory(permitLimit: 1);
        using var client = PeerClient(factory, IPAddress.Parse("198.51.100.60"));
        await ExhaustAsync(client, 1);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth/session")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/bff/api/v1/leagues/me")).StatusCode);
            // Pre-existing behavior (LogoutRejectsRequestsWithoutAntiforgeryToken): 400, never 429.
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsync("/auth/logout", new FormUrlEncodedContent([]))).StatusCode);
        }
    }

    [Fact]
    public void EveryAnonymousAuthenticationPostRequiresTheLoginRateLimitPolicy()
    {
        using var factory = CreateFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/auth/", StringComparison.Ordinal) == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .ToList();

        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            var anonymous = endpoint.Metadata.GetMetadata<IAuthorizeData>() is null ||
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            if (anonymous)
                Assert.True(policy == PlayerAuthRateLimiting.LoginPolicy,
                    $"{endpoint.RoutePattern.RawText} is an anonymous authentication POST without rate limiting.");
        }

        var login = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == LoginPath);
        Assert.Equal(PlayerAuthRateLimiting.LoginPolicy,
            login.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
        var logout = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/auth/logout");
        Assert.Null(logout.Metadata.GetMetadata<EnableRateLimitingAttribute>());
    }

    [Fact]
    public void IdentityLockoutSettingsAreUnchangedAndDefaultLimitDoesNotPreemptLockout()
    {
        using var factory = CreateFactory();
        var lockout = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout;

        Assert.Equal(5, lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), lockout.DefaultLockoutTimeSpan);
        Assert.True(lockout.AllowedForNewUsers);
        Assert.True(PlayerAuthRateLimitOptions.DefaultPermitLimit > lockout.MaxFailedAccessAttempts);
    }

    [Theory]
    [InlineData("PermitLimit", "0")]
    [InlineData("PermitLimit", "101")]
    [InlineData("WindowSeconds", "0")]
    [InlineData("WindowSeconds", "3601")]
    public void InvalidLimitConfigurationFailsStartup(string setting, string value)
    {
        using var factory = CreateFactory(extraSettings: [($"{PlayerAuthRateLimitOptions.SectionName}:{setting}", value)]);

        Assert.ThrowsAny<InvalidOperationException>(() => factory.Server);
    }

    [PlayerPostgresFact]
    public async Task IdentityLockoutStillAppliesAndRejectedAttemptsDoNotChangeAccountState()
    {
        await using var database = await PlayerTestDatabase.CreateAsync();
        const string email = "rate-limit-player@example.test";
        const string password = "correct-horse-battery-staple";
        await database.CreatePlayerAsync(email, password);

        // Lockout: five wrong passwords lock the account; the right password is then refused.
        using (var factory = database.Factory(permitLimit: 100))
        using (var client = PeerClient(factory, IPAddress.Parse("198.51.100.70")))
        {
            for (var attempt = 0; attempt < 5; attempt++)
                Assert.Equal("/login?error=1", (await AntiforgeryLoginAsync(client, email, "wrong-password-value")).Headers.Location?.OriginalString);
            Assert.Equal("/login?error=1", (await AntiforgeryLoginAsync(client, email, password)).Headers.Location?.OriginalString);
            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            Assert.True(await users.IsLockedOutAsync(user));
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
        }

        // Rejected attempts never reach Identity, so they do not increment the failure count.
        using (var factory = database.Factory(permitLimit: 3))
        using (var client = PeerClient(factory, IPAddress.Parse("198.51.100.71")))
        {
            // Antiforgery acquisition is not limited; each login POST below counts once.
            for (var attempt = 0; attempt < 2; attempt++)
                Assert.Equal("/login?error=1", (await AntiforgeryLoginAsync(client, email, "wrong-password-value")).Headers.Location?.OriginalString);
            Assert.Equal("/", (await AntiforgeryLoginAsync(client, email, password)).Headers.Location?.OriginalString);
            for (var attempt = 0; attempt < 4; attempt++)
                Assert.Equal(HttpStatusCode.TooManyRequests, (await AntiforgeryLoginAsync(client, email, "wrong-password-value")).StatusCode);

            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            Assert.False(await users.IsLockedOutAsync(user));
            Assert.Equal(0, await users.GetAccessFailedCountAsync(user));
        }
    }

    private static async Task ExhaustAsync(HttpClient client, int permits)
    {
        for (var attempt = 0; attempt < permits; attempt++)
            Assert.Equal(HttpStatusCode.BadRequest, (await PostLoginAsync(client)).StatusCode);
    }

    private static PlayerAuthRateLimitPartitionKeys CreatePartitions(int maximumTrackedClients,
        int overflowPartitions) => new(maximumTrackedClients, overflowPartitions,
        TimeSpan.FromMinutes(6), TimeProvider.System);

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client,
        string email = "nobody@example.test", string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("email", email),
                new KeyValuePair<string, string>("password", "not-a-password")
            ])
        };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> AntiforgeryLoginAsync(HttpClient client, string email, string password)
    {
        using var tokenResponse = await client.GetAsync("/auth/antiforgery");
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var cookie = tokenResponse.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
        var request = new HttpRequestMessage(HttpMethod.Post, LoginPath)
        {
            Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("email", email),
                new KeyValuePair<string, string>("password", password)
            ])
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static WebApplicationFactory<Program> CreateFactory(int? permitLimit = null, string? trustedProxy = null,
        Action<IServiceCollection>? configureServices = null, (string Key, string Value)[]? extraSettings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION",
                "Host=127.0.0.1;Database=unused;Username=unused;Password=unused;Timeout=1;Command Timeout=1");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH",
                Path.Combine(Path.GetTempPath(), "saturday-scout-player-rate-limit-test-keys"));
            if (permitLimit is not null)
            {
                builder.UseSetting($"{PlayerAuthRateLimitOptions.SectionName}:PermitLimit", permitLimit.Value.ToString());
                // Long window: tests never wait for replenishment, so they cannot flake on timing.
                builder.UseSetting($"{PlayerAuthRateLimitOptions.SectionName}:WindowSeconds", "3600");
            }
            if (trustedProxy is not null)
            {
                builder.UseSetting("TrustedReverseProxy:Enabled", "true");
                builder.UseSetting("TrustedReverseProxy:KnownProxies:0", trustedProxy);
            }
            foreach (var (key, value) in extraSettings ?? [])
                builder.UseSetting(key, value);
            if (configureServices is not null) builder.ConfigureTestServices(configureServices);
        });

    private static HttpClient PeerClient(WebApplicationFactory<Program> factory, IPAddress remoteIp) =>
        new(factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = remoteIp))
        {
            BaseAddress = new Uri("https://localhost")
        };

    private sealed class Counters
    {
        private int antiforgeryValidations;
        private int authentications;
        public int AntiforgeryValidations => Volatile.Read(ref antiforgeryValidations);
        public int Authentications => Volatile.Read(ref authentications);
        public void Antiforgery() => Interlocked.Increment(ref antiforgeryValidations);
        public void Authentication() => Interlocked.Increment(ref authentications);
    }

    private sealed class CountingAntiforgery(Counters counters) : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => new("token", "cookie", "form", "X-CSRF-TOKEN");
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => GetAndStoreTokens(httpContext);
        public Task<bool> IsRequestValidAsync(HttpContext httpContext)
        {
            counters.Antiforgery();
            return Task.FromResult(false);
        }
        public Task ValidateRequestAsync(HttpContext httpContext) => throw new AntiforgeryValidationException("invalid");
        public void SetCookieTokenAndHeader(HttpContext httpContext) { }
    }

    private sealed class CountingAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger, UrlEncoder encoder, Counters counters)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "RateLimitCounting";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            counters.Authentication();
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    private sealed class ReadyProbe : IDatabaseReadinessProbe
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}

public sealed class PlayerTestDatabase : IAsyncDisposable
{
    private readonly NpgsqlConnection admin;
    private readonly string name;
    private readonly string connection;
    private readonly string keyPath;

    private PlayerTestDatabase(NpgsqlConnection admin, string name, string connection, string keyPath)
    {
        this.admin = admin;
        this.name = name;
        this.connection = connection;
        this.keyPath = keyPath;
    }

    public static async Task<PlayerTestDatabase> CreateAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION"))
        { Database = "postgres" };
        var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var name = $"saturday_scout_player_test_{Guid.NewGuid():N}";
        await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await command.ExecuteNonQueryAsync();
        var testConnection = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        { Database = name, Pooling = false }.ConnectionString;
        var keyPath = Path.Combine(Path.GetTempPath(), $"saturday-scout-player-test-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyPath);
        var database = new PlayerTestDatabase(admin, name, testConnection, keyPath);
        await using var domain = new SaturdayScoutDbContext(new DbContextOptionsBuilder<SaturdayScoutDbContext>()
            .UseNpgsql(testConnection).Options);
        await domain.Database.MigrateAsync();
        await using var identity = new ScoutIdentityDbContext(new DbContextOptionsBuilder<ScoutIdentityDbContext>()
            .UseNpgsql(testConnection, x => x.MigrationsHistoryTable("__EFMigrationsHistory", "auth")).Options);
        await identity.Database.MigrateAsync();
        return database;
    }

    public WebApplicationFactory<Program> Factory(int permitLimit = 100) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", connection);
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", keyPath);
            builder.UseSetting($"{PlayerAuthRateLimitOptions.SectionName}:PermitLimit", permitLimit.ToString());
            builder.UseSetting($"{PlayerAuthRateLimitOptions.SectionName}:WindowSeconds", "3600");
        });

    public async Task CreatePlayerAsync(string email, string password)
    {
        using var factory = Factory();
        await using var scope = factory.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>();
        const string token = "disposable-player-invitation";
        identity.Invitations.Add(new AccountInvitation
        {
            Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(),
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token))),
            Role = ScoutRoles.Player, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
        });
        await identity.SaveChangesAsync();
        var id = await scope.ServiceProvider.GetRequiredService<AccountProvisioningService>()
            .AcceptInvitationAsync(token, email, "Rate Limit Player", password, default);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(id.ToString()))!;
        var confirmation = await users.GenerateEmailConfirmationTokenAsync(user);
        Assert.True((await users.ConfirmEmailAsync(user, confirmation)).Succeeded);
    }

    public async ValueTask DisposeAsync()
    {
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
        await command.ExecuteNonQueryAsync();
        await admin.DisposeAsync();
        Directory.Delete(keyPath, recursive: true);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PlayerPostgresFactAttribute : FactAttribute
{
    public PlayerPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION")))
            Skip = "Set SATURDAY_SCOUT_TEST_ADMIN_CONNECTION to run disposable PostgreSQL integration tests.";
    }
}
