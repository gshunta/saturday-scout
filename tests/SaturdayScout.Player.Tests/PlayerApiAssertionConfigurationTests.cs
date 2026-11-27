using System.Security.Cryptography;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using SaturdayScout.Player;
using SaturdayScout.Infrastructure.Identity;
using Xunit;

namespace SaturdayScout.Player.Tests;

public sealed class PlayerApiAssertionConfigurationTests
{
    [Fact]
    public void ProductionAcceptsMultilinePemAndIssuerSignsWithItInProcess()
    {
        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var options = ProductionOptions(pem);

        PlayerApiAssertionConfiguration.ValidateProduction(options, "https://api.example.test");
        using var issuer = new PlayerApiAssertionIssuer(Options.Create(options));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")),
            new Claim(ClaimTypes.Role, ScoutRoles.Player)
        ], "test"));

        var token = new JwtSecurityTokenHandler().ReadJwtToken(issuer.Issue(principal));
        Assert.Equal("RS256", token.Header.Alg);
        Assert.Equal(options.KeyId, token.Header.Kid);
        Assert.Equal(options.Issuer, token.Issuer);
        Assert.Contains(options.Audience, token.Audiences);
        Assert.Equal(options.ClientId, token.Claims.Single(x => x.Type == PlayerAssertionDefaults.ClientIdClaim).Value);
        Assert.Equal(options.TokenLifetimeSeconds + 5, (int)(token.ValidTo - token.ValidFrom).TotalSeconds);
    }

    [Fact]
    public void ProductionRejectsMissingPemInsteadOfFallingBackToConfiguredFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-player-no-fallback-{Guid.NewGuid():N}.pem");
        try
        {
            using var rsa = RSA.Create(3072);
            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
            var options = ProductionOptions("");
            options.PrivateKeyPath = path;

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PlayerApiAssertionConfiguration.ValidateProduction(options, "https://api.example.test"));
            Assert.DoesNotContain(path, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ProductionRejectsUnresolvedAppServiceKeyVaultReference()
    {
        var options = ProductionOptions("@Microsoft.KeyVault(SecretUri=https://vault.test/secrets/player-key)");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlayerApiAssertionConfiguration.ValidateProduction(options, "https://api.example.test"));
        Assert.Contains("did not resolve", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretUri", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsMalformedPemWithoutEchoingConfigurationValue()
    {
        var options = ProductionOptions("malformed-private-key-marker");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlayerApiAssertionConfiguration.ValidateProduction(options, "https://api.example.test"));
        Assert.DoesNotContain("malformed-private-key-marker", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsPublicOnlyPem()
    {
        using var rsa = RSA.Create(3072);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlayerApiAssertionConfiguration.ValidateProduction(
                ProductionOptions(rsa.ExportSubjectPublicKeyInfoPem()), "https://api.example.test"));
        Assert.Contains("private signing material", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN PUBLIC KEY", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TestingPreservesTheExistingFileBasedPrivateKeyConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-player-testing-{Guid.NewGuid():N}.pem");
        try
        {
            using var rsa = RSA.Create(3072);
            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
            var options = ProductionOptions("");
            options.PrivateKeyPath = path;
            PlayerApiAssertionConfiguration.ValidateTesting(options, "https://api.example.test");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PrivateRsaPemWithAtLeast3072BitsPassesStartupValidation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-player-signing-{Guid.NewGuid():N}.pem");
        try
        {
            using var rsa = RSA.Create(3072);
            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
            PlayerApiAssertionIssuer.ValidatePrivateKeyFile(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PublicOnlyRsaPemIsRejectedAsSigningMaterial()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-player-public-only-{Guid.NewGuid():N}.pem");
        try
        {
            using var rsa = RSA.Create(3072);
            File.WriteAllText(path, rsa.ExportSubjectPublicKeyInfoPem());
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PlayerApiAssertionIssuer.ValidatePrivateKeyFile(path));
            Assert.Contains("private signing material", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BEGIN PUBLIC KEY", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PrivateRsaPemBelowMinimumKeySizeIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-player-small-key-{Guid.NewGuid():N}.pem");
        try
        {
            using var rsa = RSA.Create(2048);
            File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PlayerApiAssertionIssuer.ValidatePrivateKeyFile(path));
            Assert.Contains("3072", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MalformedPemIsRejectedWithoutEchoingContents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scout-player-malformed-{Guid.NewGuid():N}.pem");
        const string marker = "private-key-secret-marker";
        try
        {
            File.WriteAllText(path, marker);
            var exception = Assert.ThrowsAny<Exception>(() => PlayerApiAssertionIssuer.ValidatePrivateKeyFile(path));
            Assert.DoesNotContain(marker, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static PlayerApiAssertionOptions ProductionOptions(string pem) => new()
    {
        Issuer = "SaturdayScout.Player",
        Audience = "SaturdayScout.Api",
        ClientId = "saturday-scout-player",
        KeyId = "test-signing-key",
        PrivateKeyPem = pem,
        TokenLifetimeSeconds = 120
    };
}
