using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Identity;
using Xunit;

namespace SaturdayScout.Api.IntegrationTests;

public sealed class PlayerAssertionAuthenticationTests
{
    [PostgresFact]
    public async Task PlayerAssertionValidatesSignatureClientLifetimeIdentityRoleAndLeagueMembership()
    {
        await using var database = await TestDatabase.Create();
        using var signingKey = RSA.Create(3072);
        using var attackerKey = RSA.Create(3072);
        var playerId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var commissioner = new User(playerId, "Player One", "player-one", "player-one@example.test");
        var outsider = new User(outsiderId, "Player Two", "player-two", "player-two@example.test");
        var league = new League(Guid.NewGuid(), "Private League", playerId, "hashed-invite", DateTimeOffset.UtcNow.AddDays(2));
        var home = new Team(Guid.NewGuid(), Sport.CollegeFootball, "Home College", "HOM", "fixture", "home");
        var away = new Team(Guid.NewGuid(), Sport.CollegeFootball, "Away College", "AWY", "fixture", "away");
        var now = DateTimeOffset.UtcNow;
        var games = Enumerable.Range(0, 10).Select(i => new Game(Guid.NewGuid(), Sport.CollegeFootball, 2026, 10,
            home.Id, away.Id, now.AddDays(1).AddHours(i), "fixture", $"college-{i}")).ToArray();
        var slate = new WeeklySlate(Guid.NewGuid(), league.Id, Sport.CollegeFootball, 2026, 10);
        slate.ReplaceGames(games);
        slate.Publish(games, now.AddHours(12), now);
        await using (var context = database.Context())
        {
            context.AddRange(commissioner, outsider, league, home, away, slate,
                new LeagueMember(league.Id, playerId, MembershipRole.Commissioner, now));
            context.AddRange(games);
            context.AddRange(slate.Games);
            await context.SaveChangesAsync();
        }

        var keyId = "player-signing-2026-01";
        var settings = new Dictionary<string, string?>
        {
            ["PlayerAssertions:Issuer"] = "SaturdayScout.Player",
            ["PlayerAssertions:Audience"] = "SaturdayScout.Api",
            ["PlayerAssertions:AuthorizedClientId"] = "saturday-scout-player",
            ["PlayerAssertions:MaximumLifetimeSeconds"] = "180",
            ["PlayerAssertions:PublicKeys:0:KeyId"] = keyId,
            ["PlayerAssertions:PublicKeys:0:PublicKeyPem"] = signingKey.ExportSubjectPublicKeyInfoPem()
        };
        using var factory = database.Factory("Production", settings);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var domainUser in new[] { commissioner, outsider })
            {
                var identityUser = new ApplicationUser
                {
                    Id = domainUser.Id, UserName = domainUser.Email, Email = domainUser.Email, EmailConfirmed = true
                };
                Assert.True((await users.CreateAsync(identityUser, "Disposable-test-Password-123!")).Succeeded);
                Assert.True((await users.AddToRoleAsync(identityUser, ScoutRoles.Player)).Succeeded);
            }
        }

        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        const string gamesPath = "/api/v1/games?sport=nfl&season=2026&week=8";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(gamesPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/leagues/me")).StatusCode);

        client.DefaultRequestHeaders.Authorization = Bearer(Issue(signingKey, keyId, playerId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(gamesPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/leagues/{league.Id}")).StatusCode);
        var myLeagues = await client.GetFromJsonAsync<LeagueDto[]>("/api/v1/leagues/me");
        Assert.Equal(league.Id, Assert.Single(myLeagues!).Id);
        var activeSlateResponse = await client.GetAsync($"/api/v1/leagues/{league.Id}/slates/active");
        Assert.Equal(HttpStatusCode.OK, activeSlateResponse.StatusCode);
        var activeSlate = await activeSlateResponse.Content.ReadFromJsonAsync<PlayerSlateDto>();
        Assert.Equal(10, activeSlate!.Games.Count);
        Assert.Equal("Home College", activeSlate.Games[0].HomeTeam);
        Assert.Equal("Away College", activeSlate.Games[0].AwayTeam);
        var picksPath = $"/api/v1/slates/{slate.Id}/picks";
        var picks = games.Select((game, index) => new PickDto(game.Id, home.Id, index + 1)).ToArray();
        var savedDraft = await client.PutAsJsonAsync(picksPath, new SavePicksRequest(picks.Take(9).ToArray(), 0));
        Assert.Equal(HttpStatusCode.OK, savedDraft.StatusCode);
        var draft = (await savedDraft.Content.ReadFromJsonAsync<PickSheetDto>())!;
        var restored = await client.GetFromJsonAsync<PickSheetDto>($"/api/v1/slates/{slate.Id}/picks/me");
        Assert.Equal(draft.Picks, restored!.Picks);
        var invalidRanks = picks.ToArray();
        invalidRanks[1] = invalidRanks[1] with { ConfidenceRank = 1 };
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PutAsJsonAsync(picksPath, new SavePicksRequest(invalidRanks, draft.Version))).StatusCode);
        var complete = await client.PutAsJsonAsync(picksPath, new SavePicksRequest(picks, draft.Version));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var readySheet = (await complete.Content.ReadFromJsonAsync<PickSheetDto>())!;
        var submittedResponse = await client.PostAsJsonAsync($"/api/v1/slates/{slate.Id}/submit",
            new SubmitRequest(readySheet.Version));
        Assert.Equal(HttpStatusCode.OK, submittedResponse.StatusCode);
        var submittedSheet = (await submittedResponse.Content.ReadFromJsonAsync<PickSheetDto>())!;
        Assert.Equal("Submitted", submittedSheet.Status);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync(picksPath, new SavePicksRequest(picks, submittedSheet.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/v1/slates/{slate.Id}/submit",
                new SubmitRequest(submittedSheet.Version))).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(Issue(signingKey, keyId, outsiderId));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/leagues/{league.Id}")).StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<LeagueDto[]>("/api/v1/leagues/me") ?? []);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/v1/leagues/{league.Id}/slates/active")).StatusCode);

        client.DefaultRequestHeaders.Authorization = Bearer(Issue(attackerKey, keyId, playerId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(gamesPath)).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(Issue(signingKey, keyId, playerId, expiresInSeconds: -120));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(gamesPath)).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(Issue(signingKey, keyId, playerId, clientId: "unapproved-client"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(gamesPath)).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(Issue(signingKey, keyId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(gamesPath)).StatusCode);
    }

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    private static string Issue(RSA rsa, string keyId, Guid subjectId, int expiresInSeconds = 120,
        string clientId = "saturday-scout-player")
    {
        var now = DateTimeOffset.UtcNow;
        var issuedAt = expiresInSeconds < 0 ? now.AddSeconds(expiresInSeconds - 60) : now;
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        var jwt = new JwtSecurityToken("SaturdayScout.Player", "SaturdayScout.Api",
            [
                new Claim(JwtRegisteredClaimNames.Sub, subjectId.ToString("D")),
                new Claim(PlayerAssertionDefaults.ClientIdClaim, clientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            ], issuedAt.AddSeconds(-5).UtcDateTime, now.AddSeconds(expiresInSeconds).UtcDateTime, credentials);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
