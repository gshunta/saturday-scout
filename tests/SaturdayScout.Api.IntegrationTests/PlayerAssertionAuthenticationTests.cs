using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
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
        await using (var context = database.Context())
        {
            context.AddRange(commissioner, outsider, league,
                new LeagueMember(league.Id, playerId, MembershipRole.Commissioner, DateTimeOffset.UtcNow));
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

        client.DefaultRequestHeaders.Authorization = Bearer(Issue(signingKey, keyId, playerId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(gamesPath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/leagues/{league.Id}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = Bearer(Issue(signingKey, keyId, outsiderId));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/leagues/{league.Id}")).StatusCode);

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
