using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task ReactRootAndClientRoutesServeTheEntryPointWithoutRedirecting()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        foreach (var route in new[] { "/react/", "/react/login", "/react/standings" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("<div id=\"root\"></div>", await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        using var bareRoot = await client.GetAsync("/react");
        Assert.Equal(HttpStatusCode.OK, bareRoot.StatusCode);
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
    public async Task SessionEndpointReturnsOnlySafePlayerSessionFields()
    {
        using var factory = CreateBffFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        var userId = Guid.NewGuid();
        AddTestPlayer(client, userId);

        using var response = await client.GetAsync("/auth/session");
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(userId.ToString(), session.GetProperty("userId").GetString());
        Assert.Equal("Scout Player", session.GetProperty("displayName").GetString());
        Assert.Equal("Player", Assert.Single(session.GetProperty("roles").EnumerateArray()).GetString());
        Assert.DoesNotContain("email", session.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", session.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredPlayerSessionReturnsMachineReadableUnauthorizedResponse()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        AddPlayerCookie(factory, client, Guid.NewGuid(), ScoutRoles.Player, DateTimeOffset.UtcNow.AddMinutes(-1));

        using var response = await client.GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(401, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
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
    public async Task LogoutClearsPlayerCookieAfterValidAntiforgeryRequest()
    {
        using var factory = CreateBffFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        AddTestPlayer(client);
        var (token, cookie) = await AcquireAntiforgeryAsync(client);
        AddCookie(client, cookie);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);

        using var response = await client.PostAsync("/auth/logout", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?signedout=1", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("__Host-SaturdayScout.Player=", StringComparison.Ordinal) && value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Remove("X-Test-Player");
        client.DefaultRequestHeaders.Remove("X-Test-Player-Id");
        client.DefaultRequestHeaders.Remove("X-Test-Player-Role");
        using var sessionResponse = await client.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, sessionResponse.StatusCode);
    }

    [Fact]
    public async Task BffRejectsUnauthenticatedRequestsWithMachineReadable401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/bff/api/v1/leagues/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(401, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task BffRequiresPlayerRoleAndRejectsAdministratorOnlyPrincipal()
    {
        using var factory = CreateBffFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        AddTestPlayer(client, Guid.NewGuid(), ScoutRoles.SystemAdministrator);

        using var response = await client.GetAsync("/bff/api/v1/leagues/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("PUT", "/bff/api/v1/slates/22222222-2222-2222-2222-222222222222/picks")]
    [InlineData("POST", "/bff/api/v1/slates/22222222-2222-2222-2222-222222222222/submit")]
    public async Task BffMutationsRejectMissingAntiforgeryTokens(string method, string path)
    {
        var fakeApi = new StubPlayerApiClient();
        using var factory = CreateBffFactory(services =>
        {
            services.RemoveAll<IPlayerApiClient>();
            services.AddSingleton<IPlayerApiClient>(fakeApi);
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        AddTestPlayer(client);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fakeApi.ForwardRequestCount);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task BffRejectsHostileOriginEvenWithValidAntiforgeryToken()
    {
        var fakeApi = new StubPlayerApiClient();
        using var factory = CreateBffFactory(services =>
        {
            services.RemoveAll<IPlayerApiClient>();
            services.AddSingleton<IPlayerApiClient>(fakeApi);
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        AddTestPlayer(client);
        var (token, cookie) = await AcquireAntiforgeryAsync(client);
        AddCookie(client, cookie);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        using var request = new HttpRequestMessage(HttpMethod.Put,
            "/bff/api/v1/slates/22222222-2222-2222-2222-222222222222/picks")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fakeApi.ForwardRequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.Unauthorized, "{\"title\":\"Bearer assertion rejected\",\"status\":401}")]
    [InlineData(HttpStatusCode.NotFound, "{\"title\":\"Missing\",\"status\":404}")]
    [InlineData(HttpStatusCode.Conflict, "{\"title\":\"Conflict\",\"status\":409}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"title\":\"Invalid picks\",\"status\":422}")]
    public async Task BffPreservesApiStatusAndErrorBody(HttpStatusCode apiStatus, string apiBody)
    {
        var fakeApi = new StubPlayerApiClient
        {
            Response = new PlayerApiForwardResponse((int)apiStatus,
                apiBody.Length == 0 ? null : "application/problem+json", Encoding.UTF8.GetBytes(apiBody))
        };
        using var factory = CreateBffFactory(services =>
        {
            services.RemoveAll<IPlayerApiClient>();
            services.AddSingleton<IPlayerApiClient>(fakeApi);
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        AddTestPlayer(client);
        var (token, cookie) = await AcquireAntiforgeryAsync(client);
        AddCookie(client, cookie);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        using var request = new HttpRequestMessage(HttpMethod.Put,
            "/bff/api/v1/slates/22222222-2222-2222-2222-222222222222/picks")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(apiStatus, response.StatusCode);
        Assert.Equal(apiBody, await response.Content.ReadAsStringAsync());
        Assert.Equal("/api/v1/slates/22222222-2222-2222-2222-222222222222/picks", fakeApi.LastRequestPath);
        Assert.False(response.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task BffHasNoArbitraryApiProxyRoute()
    {
        var fakeApi = new StubPlayerApiClient();
        using var factory = CreateBffFactory(services =>
        {
            services.RemoveAll<IPlayerApiClient>();
            services.AddSingleton<IPlayerApiClient>(fakeApi);
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        AddTestPlayer(client);

        using var response = await client.GetAsync("/bff/api/v1/admin/dashboard");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, fakeApi.ForwardRequestCount);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task BffConvertsUnexpectedProxyFailuresToGenericProblemDetails()
    {
        var fakeApi = new StubPlayerApiClient { ForwardException = new InvalidOperationException("Sensitive upstream detail") };
        using var factory = CreateBffFactory(services =>
        {
            services.RemoveAll<IPlayerApiClient>();
            services.AddSingleton<IPlayerApiClient>(fakeApi);
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        AddTestPlayer(client);

        using var response = await client.GetAsync("/bff/api/v1/leagues/me");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString());
        Assert.DoesNotContain("Sensitive upstream detail", content, StringComparison.Ordinal);
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

        var unauthorized = await client.SendAsync<object>(new HttpRequestMessage(HttpMethod.Get, "/api/v1/slates"));
        Assert.Equal(PlayerApiOutcome.AuthenticationRequired, unauthorized.Outcome);
        Assert.Equal("The Player API did not authorize this request. Your Player session may still be active; refresh or contact support.",
            unauthorized.Message);
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

    [Fact]
    public async Task ApiForwardingKeepsRs256AssertionOnServerToServerRequest()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, ScoutRoles.Player)
        ], "player-cookie"));
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"result\":\"ok\"}", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test") };
        var client = new PlayerApiClient(http, new FixedAuthenticationStateProvider(principal), new StubAssertionIssuer());

        var result = await client.ForwardAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/leagues/me"), principal);

        Assert.Equal((int)HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("{\"result\":\"ok\"}", Encoding.UTF8.GetString(result.Body));
        Assert.DoesNotContain("signed-test-assertion", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ApiForwardingPreservesUnauthorizedStatusReturnedByTheUpstreamApi()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")),
            new Claim(ClaimTypes.Role, ScoutRoles.Player)
        ], "player-cookie"));
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"title\":\"Bearer assertion rejected\",\"status\":401}",
                Encoding.UTF8, "application/problem+json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test") };
        var client = new PlayerApiClient(http, new FixedAuthenticationStateProvider(principal), new StubAssertionIssuer());

        var result = await client.ForwardAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/leagues/me"), principal);

        Assert.Equal((int)HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Contains("Bearer assertion rejected", Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    private static WebApplicationFactory<Program> CreateFactory(Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION",
                "Host=127.0.0.1;Database=not_used;Username=not_used;Password=not_used");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", Path.Combine(Path.GetTempPath(), "saturday-scout-player-test-keys"));
            if (configureServices is not null) builder.ConfigureTestServices(configureServices);
        });

    private static WebApplicationFactory<Program> CreateBffFactory(Action<IServiceCollection>? configureServices = null) =>
        CreateFactory(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PlayerBoundaryTestHandler>(
                "PlayerBoundaryTest", _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = "PlayerBoundaryTest";
                options.DefaultChallengeScheme = "PlayerBoundaryTest";
                options.DefaultForbidScheme = "PlayerBoundaryTest";
            });
            configureServices?.Invoke(services);
        });

    private static void AddTestPlayer(HttpClient client, Guid? userId = null, string role = ScoutRoles.Player)
    {
        client.DefaultRequestHeaders.Add("X-Test-Player", "true");
        client.DefaultRequestHeaders.Add("X-Test-Player-Id", (userId ?? Guid.NewGuid()).ToString("D"));
        client.DefaultRequestHeaders.Add("X-Test-Player-Role", role);
    }

    private static void AddPlayerCookie(WebApplicationFactory<Program> factory, HttpClient client, Guid userId,
        string role = ScoutRoles.Player, DateTimeOffset? expiresUtc = null)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(PlayerAuthDefaults.Scheme);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString("D")),
            new(ClaimTypes.Role, role),
            new(ClaimTypes.GivenName, "Scout Player")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, PlayerAuthDefaults.Scheme));
        var properties = new AuthenticationProperties { ExpiresUtc = expiresUtc ?? DateTimeOffset.UtcNow.AddMinutes(10) };
        var ticket = new AuthenticationTicket(principal, properties, PlayerAuthDefaults.Scheme);
        var protectedTicket = options.TicketDataFormat!.Protect(ticket);
        AddCookie(client, $"{options.Cookie.Name}={protectedTicket}");
    }

    private static void AddCookie(HttpClient client, string cookie)
    {
        var existing = client.DefaultRequestHeaders.TryGetValues("Cookie", out var values)
            ? values.Single()
            : null;
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", string.IsNullOrWhiteSpace(existing) ? cookie : existing + "; " + cookie);
    }

    private static async Task<(string Token, string Cookie)> AcquireAntiforgeryAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
        return (payload.GetProperty("token").GetString()!, cookie);
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private sealed class PlayerBoundaryTestHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!string.Equals(Request.Headers["X-Test-Player"], "true", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var userId = Request.Headers["X-Test-Player-Id"].ToString();
            if (!Guid.TryParse(userId, out _)) userId = Guid.NewGuid().ToString("D");
            var role = Request.Headers["X-Test-Player-Role"].FirstOrDefault() ?? ScoutRoles.Player;
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.GivenName, "Scout Player"),
                new Claim(ClaimTypes.Role, role)
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class StubAssertionIssuer : IPlayerApiAssertionIssuer
    {
        public string Issue(ClaimsPrincipal player) => "signed-test-assertion";
    }

    private sealed class StubPlayerApiClient : IPlayerApiClient
    {
        public int ForwardRequestCount { get; private set; }
        public string? LastRequestPath { get; private set; }
        public Exception? ForwardException { get; init; }
        public PlayerApiForwardResponse Response { get; set; } = new((int)HttpStatusCode.OK,
            "application/json", Encoding.UTF8.GetBytes("{}"));

        public Task<PlayerApiResult<T>> SendAsync<T>(HttpRequestMessage request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The BFF proxy test double only supports raw forwarding.");

        public Task<PlayerApiForwardResponse> ForwardAsync(HttpRequestMessage request,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            ForwardRequestCount++;
            LastRequestPath = request.RequestUri?.OriginalString;
            if (ForwardException is not null) throw ForwardException;
            return Task.FromResult(Response);
        }
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
