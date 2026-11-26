using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SaturdayScout.Infrastructure.Identity;
using Xunit;

namespace SaturdayScout.Api.IntegrationTests;

public sealed class PlayerAssertionPrevalidationTests
{
    [Fact]
    public void ApiHostUsesTheSharedAdminApiDataProtectionApplicationName()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"scout-api-dp-{Guid.NewGuid():N}");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
                builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", keyPath);
            });

            Assert.Equal(ScoutIdentityServices.DataProtectionApplicationName,
                factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        }
        finally
        {
            if (Directory.Exists(keyPath)) Directory.Delete(keyPath, recursive: true);
        }
    }

    [Fact]
    public async Task TrustedReverseProxyHttpsSchemePreventsApiRedirectLoop()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"saturday-scout-api-proxy-test-{Guid.NewGuid():N}");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("SATURDAY_SCOUT_DB_CONNECTION", "Host=127.0.0.1;Database=unused;Username=unused;Password=unused");
                builder.UseSetting("SATURDAY_SCOUT_DP_KEYS_PATH", keyPath);
                builder.UseSetting("TrustedReverseProxy:Enabled", "true");
                builder.UseSetting("TrustedReverseProxy:KnownProxies:0", IPAddress.Loopback.ToString());
                builder.UseSetting("https_port", "443");
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("http://localhost"),
                AllowAutoRedirect = false
            });
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

            using var response = await client.GetAsync("/api/v1/games?sport=nfl&season=2026&week=8");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        finally
        {
            if (Directory.Exists(keyPath)) Directory.Delete(keyPath, recursive: true);
        }
    }

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
            ConfigureProductionDataProtection(builder, keyPath);
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
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(trustedKey, keyId, 120,
            issuer: "untrusted-player"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Directory.Delete(keyPath, recursive: true);
    }

    private static string CreateToken(RSA rsa, string keyId, int expiresInSeconds,
        string clientId = "saturday-scout-player", string audience = "SaturdayScout.Api",
        string issuer = "SaturdayScout.Player")
    {
        var now = DateTimeOffset.UtcNow;
        var issuedAt = expiresInSeconds < 0 ? now.AddSeconds(expiresInSeconds - 60) : now;
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        var jwt = new JwtSecurityToken(issuer, audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString("D")),
                new Claim(PlayerAssertionDefaults.ClientIdClaim, clientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            ], issuedAt.AddSeconds(-5).UtcDateTime, now.AddSeconds(expiresInSeconds).UtcDateTime, credentials);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static void ConfigureProductionDataProtection(IWebHostBuilder builder, string testKeyPath)
    {
        builder.UseSetting("SATURDAY_SCOUT_DP_PROVIDER", "AzureBlob");
        builder.UseSetting("SATURDAY_SCOUT_DP_KEY_BLOB_URI", "https://scout-test.blob.core.windows.net/dataprotection/api.xml");
        builder.UseSetting("SATURDAY_SCOUT_DP_KEY_VAULT_KEY_ID", "https://scout-test.vault.azure.net/keys/api-protection");
        builder.UseSetting("SATURDAY_SCOUT_DP_APPLICATION_NAME", ScoutIdentityServices.DataProtectionApplicationName);
        builder.ConfigureTestServices(services =>
        {
            services.AddDataProtection()
                .SetApplicationName(ScoutIdentityServices.DataProtectionApplicationName)
                .PersistKeysToFileSystem(new DirectoryInfo(testKeyPath));
            services.PostConfigure<KeyManagementOptions>(options => options.XmlEncryptor = null);
        });
    }
}
