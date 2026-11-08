using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Api.IntegrationTests;

public sealed class ApiFlowTests
{
    [Fact]
    public async Task AdministratorPolicyRequiresRoleAndMfa()
    {
        using var apiKey = System.Security.Cryptography.RSA.Create(3072);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", Path.GetTempPath());
            builder.UseSetting("PlayerAssertions:PublicKeys:0:KeyId", "api-policy-test-key");
            builder.UseSetting("PlayerAssertions:PublicKeys:0:PublicKeyPem", apiKey.ExportSubjectPublicKeyInfoPem());
        });
        using var scope = factory.Services.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var roleOnly = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "SystemAdministrator")
        ], "test"));
        Assert.False((await authorizer.AuthorizeAsync(roleOnly, "SystemAdministrator")).Succeeded);
        var player = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "Player"),
            new Claim("amr", "mfa")
        ], "test"));
        Assert.False((await authorizer.AuthorizeAsync(player, "SystemAdministrator")).Succeeded);
        // A claim alone cannot satisfy the policy without a linked, enrolled Identity user.
        // The policy carries a database-backed requirement; the test does not call it with
        // a fictitious linked account against the dummy connection.
        var policy = await scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync("SystemAdministrator");
        Assert.Contains(policy!.Requirements, x => x is SaturdayScout.Infrastructure.Identity.SystemAdministratorMfaRequirement);
        var commissionerWithoutLeague = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, "LeagueCommissioner")
        ], "test"));
        Assert.False((await authorizer.AuthorizeAsync(commissionerWithoutLeague, "LeagueCommissioner")).Succeeded);
    }

    [PostgresFact]
    public async Task AuthorizationSubmissionConcurrencyDeadlinesAndStandings()
    {
        await using var database = await TestDatabase.Create();
        await using var seed = database.Context();
        var commissioner = new User(Guid.NewGuid(), "Commissioner", "commissioner");
        var member = new User(Guid.NewGuid(), "Member", "member");
        var outsider = new User(Guid.NewGuid(), "Outsider", "outsider");
        var home = new Team(Guid.NewGuid(), Sport.Nfl, "Home", "HOM", "fixture", "home");
        var away = new Team(Guid.NewGuid(), Sport.Nfl, "Away", "AWY", "fixture", "away");
        var now = DateTimeOffset.UtcNow;
        var games = Enumerable.Range(0, 10).Select(i => new Game(Guid.NewGuid(), Sport.Nfl, 2026, 8,
            home.Id, away.Id, now.AddHours(2 + i), "fixture", $"game-{i}")).ToArray();
        seed.AddRange(commissioner, member, outsider, home, away);
        seed.AddRange(games);
        await seed.SaveChangesAsync();

        using var factory = database.Factory("Development");
        using var noAuth = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noAuth.GetAsync("/api/v1/games?sport=nfl&season=2026&week=8")).StatusCode);
        using var commissionerClient = Client(factory, "commissioner");
        using var memberClient = Client(factory, "member");
        using var outsiderClient = Client(factory, "outsider");
        var created = await commissionerClient.PostAsJsonAsync("/api/v1/leagues", new CreateLeagueRequest("Friends"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var league = (await created.Content.ReadFromJsonAsync<LeagueDto>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/v1/leagues/{league.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await commissionerClient.PostAsJsonAsync($"/api/v1/leagues/{league.Id}/members",
            new AddMemberRequest("member"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PostAsJsonAsync($"/api/v1/leagues/{league.Id}/slates",
            new CreateSlateRequest("nfl", 2026, 8))).StatusCode);

        var slateResponse = await commissionerClient.PostAsJsonAsync($"/api/v1/leagues/{league.Id}/slates",
            new CreateSlateRequest("nfl", 2026, 8));
        Assert.Equal(HttpStatusCode.Created, slateResponse.StatusCode);
        var slate = (await slateResponse.Content.ReadFromJsonAsync<SlateDto>())!;
        var gamesResponse = await commissionerClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/games",
            new SetSlateGamesRequest(games.Select(g => g.Id).ToArray()));
        Assert.Equal(HttpStatusCode.OK, gamesResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await commissionerClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/games",
            new SetSlateGamesRequest(games.Reverse().Select(g => g.Id).ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await commissionerClient.PostAsJsonAsync($"/api/v1/slates/{slate.Id}/publish",
            new PublishSlateRequest(now.AddHours(1)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/v1/games?sport=nfl&season=2026&week=8")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/v1/slates/{slate.Id}")).StatusCode);

        var picks = games.Select((g, i) => new PickDto(g.Id, home.Id, i + 1)).ToArray();
        var badTeam = picks.ToArray();
        badTeam[0] = badTeam[0] with { SelectedTeamId = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await memberClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/picks",
            new SavePicksRequest(badTeam, null))).StatusCode);

        var draftResponse = await memberClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/picks",
            new SavePicksRequest(picks.Take(9).ToArray(), null));
        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        var draft = (await draftResponse.Content.ReadFromJsonAsync<PickSheetDto>())!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await memberClient.PostAsJsonAsync($"/api/v1/slates/{slate.Id}/submit",
            new { expectedVersion = draft.Version })).StatusCode);

        var duplicateRanks = picks.ToArray();
        duplicateRanks[1] = duplicateRanks[1] with { ConfidenceRank = 1 };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await memberClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/picks",
            new SavePicksRequest(duplicateRanks, draft.Version))).StatusCode);
        var fullResponse = await memberClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/picks",
            new SavePicksRequest(picks, draft.Version));
        Assert.Equal(HttpStatusCode.OK, fullResponse.StatusCode);
        var full = (await fullResponse.Content.ReadFromJsonAsync<PickSheetDto>())!;
        var revised = picks.ToArray();
        revised[0] = revised[0] with { SelectedTeamId = away.Id };
        var revision = await memberClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/picks",
            new SavePicksRequest(revised, full.Version));
        Assert.Equal(HttpStatusCode.OK, revision.StatusCode);
        var revisedSheet = (await revision.Content.ReadFromJsonAsync<PickSheetDto>())!;
        var restored = await memberClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/picks",
            new SavePicksRequest(picks, revisedSheet.Version));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var ready = (await restored.Content.ReadFromJsonAsync<PickSheetDto>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await memberClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/picks",
            new SavePicksRequest(picks, full.Version))).StatusCode);

        var firstSubmit = memberClient.PostAsJsonAsync($"/api/v1/slates/{slate.Id}/submit", new { expectedVersion = ready.Version });
        var secondSubmit = memberClient.PostAsJsonAsync($"/api/v1/slates/{slate.Id}/submit", new { expectedVersion = ready.Version });
        var submitted = await Task.WhenAll(firstSubmit, secondSubmit);
        Assert.Single(submitted, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(submitted, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, (await memberClient.PostAsJsonAsync($"/api/v1/slates/{slate.Id}/submit",
            new { expectedVersion = ready.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await memberClient.PutAsJsonAsync($"/api/v1/slates/{slate.Id}/picks",
            new SavePicksRequest(picks, ready.Version + 1))).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var importer = scope.ServiceProvider.GetRequiredService<IResultImportService>();
            foreach (var game in games)
                await importer.UpdateResultAsync(new ImportedResult(game.Id, GameStatus.Final, 21, 10), CancellationToken.None);
        }
        var board = (await (await memberClient.GetAsync($"/api/v1/leagues/{league.Id}/leaderboard?scope=nfl&season=2026&week=8"))
            .Content.ReadFromJsonAsync<LeaderboardDto>())!;
        Assert.Equal(55, board.Entries.Single(x => x.UserId == member.Id).Points);
        Assert.Equal(0, board.Entries.Single(x => x.UserId == commissioner.Id).Points);

        var lateGames = Enumerable.Range(0, 10).Select(i => new Game(Guid.NewGuid(), Sport.Nfl, 2026, 8,
            home.Id, away.Id, now.AddHours(2 + i), "fixture", $"late-game-{i}")).ToArray();
        seed.AddRange(lateGames);
        await seed.SaveChangesAsync();
        var lateLeagueResponse = await commissionerClient.PostAsJsonAsync("/api/v1/leagues", new CreateLeagueRequest("Late"));
        var lateLeague = (await lateLeagueResponse.Content.ReadFromJsonAsync<LeagueDto>())!;
        var lateSlateResponse = await commissionerClient.PostAsJsonAsync($"/api/v1/leagues/{lateLeague.Id}/slates",
            new CreateSlateRequest("nfl", 2026, 8));
        var lateSlate = (await lateSlateResponse.Content.ReadFromJsonAsync<SlateDto>())!;
        Assert.Equal(HttpStatusCode.OK, (await commissionerClient.PutAsJsonAsync($"/api/v1/slates/{lateSlate.Id}/games",
            new SetSlateGamesRequest(lateGames.Select(g => g.Id).ToArray()))).StatusCode);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        Assert.Equal(HttpStatusCode.OK, (await commissionerClient.PostAsJsonAsync($"/api/v1/slates/{lateSlate.Id}/publish",
            new PublishSlateRequest(deadline))).StatusCode);
        var latePicks = lateGames.Select((g, i) => new PickDto(g.Id, home.Id, i + 1)).ToArray();
        var lateDraftResponse = await commissionerClient.PutAsJsonAsync($"/api/v1/slates/{lateSlate.Id}/picks",
            new SavePicksRequest(latePicks, null));
        Assert.Equal(HttpStatusCode.OK, lateDraftResponse.StatusCode);
        var lateDraft = (await lateDraftResponse.Content.ReadFromJsonAsync<PickSheetDto>())!;
        var wait = deadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(200);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        Assert.Equal(HttpStatusCode.Conflict, (await commissionerClient.PutAsJsonAsync($"/api/v1/slates/{lateSlate.Id}/picks",
            new SavePicksRequest(latePicks, lateDraft.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await commissionerClient.PostAsJsonAsync($"/api/v1/slates/{lateSlate.Id}/submit",
            new { expectedVersion = lateDraft.Version })).StatusCode);
    }

    [Fact]
    public async Task ProductionRejectsDevelopmentIdentityAndHidesOpenApi()
    {
        using var apiKey = System.Security.Cryptography.RSA.Create(3072);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", Path.GetTempPath());
            builder.UseSetting("PlayerAssertions:PublicKeys:0:KeyId", "api-policy-test-key");
            builder.UseSetting("PlayerAssertions:PublicKeys:0:PublicKeyPem", apiKey.ExportSubjectPublicKeyInfoPem());
        });
        using var client = Client(factory, "commissioner");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/games?sport=nfl&season=2026&week=8")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string subject)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        return client;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION")))
            Skip = "Set SATURDAY_SCOUT_TEST_ADMIN_CONNECTION to run API integration tests.";
    }
}

public sealed class TestDatabase : IAsyncDisposable
{
    private readonly NpgsqlConnection _admin;
    private readonly string _name;
    private readonly string _connection;
    private readonly string _keyPath;
    private readonly System.Security.Cryptography.RSA _playerAssertionKey;
    private const string PlayerAssertionKeyId = "api-integration-test-key";

    private TestDatabase(NpgsqlConnection admin, string name, string connection, string keyPath,
        System.Security.Cryptography.RSA playerAssertionKey)
    {
        _admin = admin;
        _name = name;
        _connection = connection;
        _keyPath = keyPath;
        _playerAssertionKey = playerAssertionKey;
    }

    public static async Task<TestDatabase> Create()
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION"))
        {
            Database = "postgres"
        };
        var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        var name = $"saturday_scout_api_test_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();
        var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = name, Pooling = false };
        var keyPath = Path.Combine(Path.GetTempPath(), $"saturday-scout-api-test-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyPath);
        var database = new TestDatabase(admin, name, testBuilder.ConnectionString, keyPath,
            System.Security.Cryptography.RSA.Create(3072));
        await using var context = database.Context();
        await context.Database.MigrateAsync();
        await using var identity = database.IdentityContext();
        await identity.Database.MigrateAsync();
        return database;
    }

    public SaturdayScoutDbContext Context() => new(new DbContextOptionsBuilder<SaturdayScoutDbContext>().UseNpgsql(_connection).Options);
    public ScoutIdentityDbContext IdentityContext() => new(new DbContextOptionsBuilder<ScoutIdentityDbContext>()
        .UseNpgsql(_connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "auth")).Options);

    public WebApplicationFactory<Program> Factory(string environment,
        IReadOnlyDictionary<string, string?>? additionalSettings = null) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", _connection);
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", _keyPath);
            builder.UseSetting("PlayerAssertions:PublicKeys:0:KeyId", PlayerAssertionKeyId);
            builder.UseSetting("PlayerAssertions:PublicKeys:0:PublicKeyPem", _playerAssertionKey.ExportSubjectPublicKeyInfoPem());
            if (additionalSettings is not null)
                foreach (var (key, value) in additionalSettings)
                    if (value is not null) builder.UseSetting(key, value);
        });

    public async ValueTask DisposeAsync()
    {
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", _admin);
        await drop.ExecuteNonQueryAsync();
        await _admin.DisposeAsync();
        _playerAssertionKey.Dispose();
        Directory.Delete(_keyPath, recursive: true);
    }
}
