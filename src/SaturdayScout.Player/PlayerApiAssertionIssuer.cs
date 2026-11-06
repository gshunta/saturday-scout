using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SaturdayScout.Infrastructure.Identity;

namespace SaturdayScout.Player;

public interface IPlayerApiAssertionIssuer
{
    string Issue(ClaimsPrincipal player);
}

public sealed class PlayerApiAssertionIssuer : IPlayerApiAssertionIssuer, IDisposable
{
    private const int MinimumRsaKeySize = 3072;
    private readonly PlayerApiAssertionOptions _options;
    private readonly RSA _privateKey;

    public PlayerApiAssertionIssuer(IOptions<PlayerApiAssertionOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.Issuer) || string.IsNullOrWhiteSpace(_options.Audience) ||
            string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.KeyId) ||
            !Path.IsPathFullyQualified(_options.PrivateKeyPath) || _options.TokenLifetimeSeconds is < 30 or > 180)
            throw new InvalidOperationException("Player API assertion configuration is incomplete or invalid.");

        _privateKey = RSA.Create();
        try
        {
            ImportAndValidatePrivateKey(_privateKey, _options.PrivateKeyPath);
        }
        catch
        {
            _privateKey.Dispose();
            throw;
        }
    }

    public static void ValidatePrivateKeyFile(string path)
    {
        using var key = RSA.Create();
        ImportAndValidatePrivateKey(key, path);
    }

    private static void ImportAndValidatePrivateKey(RSA key, string path)
    {
        key.ImportFromPem(File.ReadAllText(path));
        if (key.KeySize < MinimumRsaKeySize)
            throw new InvalidOperationException("The Player API assertion RSA key must be at least 3072 bits.");
    }

    public string Issue(ClaimsPrincipal player)
    {
        if (player.Identity?.IsAuthenticated != true || !player.IsInRole(ScoutRoles.Player) ||
            !Guid.TryParse(player.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            throw new InvalidOperationException("A validated Player session is required to issue an API assertion.");

        var now = DateTimeOffset.UtcNow;
        var credentials = new SigningCredentials(new RsaSecurityKey(_privateKey) { KeyId = _options.KeyId },
            SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
                new Claim(PlayerAssertionDefaults.ClientIdClaim, _options.ClientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            ],
            notBefore: now.AddSeconds(-5).UtcDateTime,
            expires: now.AddSeconds(_options.TokenLifetimeSeconds).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose() => _privateKey.Dispose();
}
