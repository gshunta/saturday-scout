using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Player;
using Xunit;

namespace SaturdayScout.Player.Tests;

public sealed class PlayerAuthenticationBoundaryTests
{
    [Fact]
    public void PlayerUsesDedicatedSecureCookieAndScheme()
    {
        using var factory = CreateFactory();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(PlayerAuthDefaults.Scheme);

        Assert.NotEqual("Identity.Application", PlayerAuthDefaults.Scheme);
        Assert.Equal("__Host-SaturdayScout.Player", options.Cookie.Name);
        Assert.Equal("/", options.Cookie.Path);
        Assert.Null(options.Cookie.Domain);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(TimeSpan.FromMinutes(480), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
    }

    [Fact]
    public async Task AdminCookieDoesNotAuthenticatePlayerSession()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", "__Host-SaturdayScout=not-a-player-session");

        var response = await client.GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlayerPicksRouteRedirectsUnauthenticatedVisitorsToLogin()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/picks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PwaRootLaunchRouteRedirectsUnauthenticatedPlayersToLoginInsteadOfNotFound()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = true
        });

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/login", response.RequestMessage?.RequestUri?.AbsolutePath);
        Assert.DoesNotContain("<h3>Not Found</h3>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sign in", html);
    }

    [Fact]
    public async Task LoginRejectsPostsWithoutAntiforgeryToken()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent([
            new KeyValuePair<string, string>("email", "player@example.test"),
            new KeyValuePair<string, string>("password", "not-a-password")
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LogoutRejectsRequestsWithoutAntiforgeryToken()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.PostAsync("/auth/logout", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void PlayerAssertionIsShortLivedAndContainsOnlyServerBoundIdentity()
    {
        using var rsa = RSA.Create(3072);
        var keyPath = Path.Combine(Path.GetTempPath(), $"saturday-scout-player-signing-{Guid.NewGuid():N}.pem");
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var issuer = new PlayerApiAssertionIssuer(Options.Create(new PlayerApiAssertionOptions
            {
                Issuer = "SaturdayScout.Player",
                Audience = "SaturdayScout.Api",
                ClientId = "saturday-scout-player",
                KeyId = "test-key-1",
                PrivateKeyPath = keyPath,
                TokenLifetimeSeconds = 120
            }));
            var userId = Guid.NewGuid();
            var player = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, ScoutRoles.Player)
            ], "test"));

            var assertion = issuer.Issue(player);
            var privatePem = File.ReadAllText(keyPath);
            Assert.DoesNotContain(privatePem, assertion, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", assertion, StringComparison.Ordinal);
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(assertion);

            Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Header.Alg);
            Assert.Equal("test-key-1", jwt.Header.Kid);
            Assert.Equal("SaturdayScout.Player", jwt.Issuer);
            Assert.Equal("SaturdayScout.Api", Assert.Single(jwt.Audiences));
            Assert.Equal(userId.ToString("D"), jwt.Subject);
            Assert.Equal("saturday-scout-player", jwt.Claims.Single(x => x.Type == PlayerAssertionDefaults.ClientIdClaim).Value);
            Assert.InRange((jwt.ValidTo - jwt.IssuedAt).TotalSeconds, 115, 120);
            Assert.DoesNotContain(jwt.Claims, claim => claim.Type == ClaimTypes.Role || claim.Type == "role");
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void AssertionIssuerRejectsNonPlayerAndUnmappedPrincipals()
    {
        using var rsa = RSA.Create(3072);
        var keyPath = Path.Combine(Path.GetTempPath(), $"saturday-scout-player-signing-{Guid.NewGuid():N}.pem");
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        try
        {
            using var issuer = new PlayerApiAssertionIssuer(Options.Create(new PlayerApiAssertionOptions
            {
                Issuer = "SaturdayScout.Player", Audience = "SaturdayScout.Api", ClientId = "player",
                KeyId = "key-1", PrivateKeyPath = keyPath
            }));
            var nonPlayer = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, ScoutRoles.SystemAdministrator)
            ], "test"));
            Assert.Throws<InvalidOperationException>(() => issuer.Issue(nonPlayer));
            Assert.Throws<InvalidOperationException>(() => issuer.Issue(new ClaimsPrincipal()));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task ApiClientMapsAuthenticationValidationConflictAndTransientFailures()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, ScoutRoles.Player)
        ], "player-cookie"));
        var responses = new Queue<HttpResponseMessage>([
            new(HttpStatusCode.Unauthorized),
            new(HttpStatusCode.Forbidden),
            new(HttpStatusCode.Conflict),
            new(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("{\"detail\":\"Pick validation failed.\",\"errors\":{\"picks\":[\"bad rank\"]}}",
                    System.Text.Encoding.UTF8, "application/problem+json")
            },
            new(HttpStatusCode.ServiceUnavailable),
            new(HttpStatusCode.ServiceUnavailable)
        ]);
        using var handler = new StubHandler(_ => responses.Dequeue());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test") };
        var client = new PlayerApiClient(http, new FixedAuthenticationStateProvider(principal), new StubAssertionIssuer());

        Assert.Equal(PlayerApiOutcome.AuthenticationRequired,
            (await client.SendAsync<object>(new HttpRequestMessage(HttpMethod.Get, "/api/v1/slates"))).Outcome);
        Assert.Equal(PlayerApiOutcome.Forbidden,
            (await client.SendAsync<object>(new HttpRequestMessage(HttpMethod.Get, "/api/v1/slates"))).Outcome);
        Assert.Equal(PlayerApiOutcome.Conflict,
            (await client.SendAsync<object>(new HttpRequestMessage(HttpMethod.Put, "/api/v1/slates/1/picks"))).Outcome);
        var invalid = await client.SendAsync<object>(new HttpRequestMessage(HttpMethod.Put, "/api/v1/slates/1/picks"));
        Assert.Equal(PlayerApiOutcome.ValidationFailed, invalid.Outcome);
        Assert.Equal("bad rank", invalid.ValidationErrors!["picks"][0]);
        var readFailure = await client.SendAsync<object>(new HttpRequestMessage(HttpMethod.Get, "/api/v1/slates"));
        Assert.Equal(PlayerApiOutcome.TransientFailure, readFailure.Outcome);
        Assert.True(readFailure.RetryRecommended);
        var writeFailure = await client.SendAsync<object>(new HttpRequestMessage(HttpMethod.Post, "/api/v1/slates/1/submit"));
        Assert.Equal(PlayerApiOutcome.TransientFailure, writeFailure.Outcome);
        Assert.False(writeFailure.RetryRecommended);
        Assert.Equal(6, handler.RequestCount);
    }

    [Fact]
    public async Task ApiClientRejectsAbsoluteRequestTargets()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, ScoutRoles.Player)
        ], "player-cookie"));
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test") };
        var client = new PlayerApiClient(http, new FixedAuthenticationStateProvider(principal), new StubAssertionIssuer());

        var result = await client.SendAsync<object>(new HttpRequestMessage(HttpMethod.Get, "https://evil.example.test/api/v1/games"));

        Assert.Equal(PlayerApiOutcome.Failure, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION",
                "Host=127.0.0.1;Database=not_used;Username=not_used;Password=not_used");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", Path.Combine(Path.GetTempPath(), "saturday-scout-player-test-keys"));
        });

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class StubAssertionIssuer : IPlayerApiAssertionIssuer
    {
        public string Issue(ClaimsPrincipal player) => "signed-test-assertion";
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("signed-test-assertion", request.Headers.Authorization?.Parameter);
            return Task.FromResult(responder(request));
        }
    }
}
