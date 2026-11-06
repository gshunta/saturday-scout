using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using SaturdayScout.Infrastructure.Identity;
using Xunit;

namespace SaturdayScout.Api.IntegrationTests;

public sealed class PlayerAssertionPrevalidationTests
{
    [Fact]
    public void ProductionRequiresRotatableRsaKeyRingWhileDevelopmentCanRetainItsExistingAuthOnly()
    {
        var options = new PlayerAssertionApiOptions();
        Assert.Throws<InvalidOperationException>(() => PlayerAssertionKeyRing.Create(options, isDevelopment: false));
        using var development = PlayerAssertionKeyRing.Create(options, isDevelopment: true);
        Assert.Empty(development.All);

        using var first = RSA.Create(3072);
        using var second = RSA.Create(3072);
        options.PublicKeys =
        [
            new PlayerAssertionPublicKeyOptions { KeyId = "old", PublicKeyPem = first.ExportSubjectPublicKeyInfoPem() },
            new PlayerAssertionPublicKeyOptions { KeyId = "new", PublicKeyPem = second.ExportSubjectPublicKeyInfoPem() }
        ];
        using var rotationRing = PlayerAssertionKeyRing.Create(options, isDevelopment: false);
        Assert.Single(rotationRing.Resolve("old"));
        Assert.Single(rotationRing.Resolve("new"));
        Assert.Empty(rotationRing.Resolve("unknown"));
    }

    [Fact]
    public async Task AnonymousForgedAndExpiredAssertionsAreRejectedBeforeDatabaseLookup()
    {
        using var trustedKey = RSA.Create(3072);
        using var attackerKey = RSA.Create(3072);
        var keyPath = Path.Combine(Path.GetTempPath(), $"saturday-scout-api-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyPath);
        var keyId = "trusted-key";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", keyPath);
            builder.UseSetting("PlayerAssertions:Issuer", "SaturdayScout.Player");
            builder.UseSetting("PlayerAssertions:Audience", "SaturdayScout.Api");
            builder.UseSetting("PlayerAssertions:AuthorizedClientId", "saturday-scout-player");
            builder.UseSetting("PlayerAssertions:MaximumLifetimeSeconds", "180");
            builder.UseSetting("PlayerAssertions:PublicKeys:0:KeyId", keyId);
            builder.UseSetting("PlayerAssertions:PublicKeys:0:PublicKeyPem", trustedKey.ExportSubjectPublicKeyInfoPem());
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        const string path = "/api/v1/games?sport=nfl&season=2026&week=8";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(attackerKey, keyId, 120));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(trustedKey, keyId, -120));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(trustedKey, keyId, 120,
            clientId: "unapproved-client"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(trustedKey, keyId, 120,
            audience: "another-api"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Directory.Delete(keyPath, recursive: true);
    }

    private static string CreateToken(RSA rsa, string keyId, int expiresInSeconds,
        string clientId = "saturday-scout-player", string audience = "SaturdayScout.Api")
    {
        var now = DateTimeOffset.UtcNow;
        var issuedAt = expiresInSeconds < 0 ? now.AddSeconds(expiresInSeconds - 60) : now;
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        var jwt = new JwtSecurityToken("SaturdayScout.Player", audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString("D")),
                new Claim(PlayerAssertionDefaults.ClientIdClaim, clientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            ], issuedAt.AddSeconds(-5).UtcDateTime, now.AddSeconds(expiresInSeconds).UtcDateTime, credentials);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
