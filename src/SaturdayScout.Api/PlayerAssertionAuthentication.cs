using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Api;

public sealed class PlayerAssertionApiOptions
{
    public const string SectionName = "PlayerAssertions";
    public string Issuer { get; set; } = "SaturdayScout.Player";
    public string Audience { get; set; } = "SaturdayScout.Api";
    public string AuthorizedClientId { get; set; } = "saturday-scout-player";
    public int MaximumLifetimeSeconds { get; set; } = 180;
    public List<PlayerAssertionPublicKeyOptions> PublicKeys { get; set; } = [];
}

public sealed class PlayerAssertionPublicKeyOptions
{
    public string KeyId { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
}

public sealed class PlayerAssertionKeyRing : IDisposable
{
    private readonly IReadOnlyDictionary<string, RsaSecurityKey> keys;

    private PlayerAssertionKeyRing(IReadOnlyDictionary<string, RsaSecurityKey> keys) => this.keys = keys;

    public IEnumerable<SecurityKey> All => keys.Values;
    public IEnumerable<SecurityKey> Resolve(string? keyId) =>
        keyId is not null && keys.TryGetValue(keyId, out var key) ? [key] : [];

    public static PlayerAssertionKeyRing Create(PlayerAssertionApiOptions options, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience) ||
            string.IsNullOrWhiteSpace(options.AuthorizedClientId) || options.MaximumLifetimeSeconds is < 30 or > 300)
            throw new InvalidOperationException("Player assertion API configuration is invalid.");
        if (!isDevelopment && options.PublicKeys.Count == 0)
            throw new InvalidOperationException("At least one Player assertion public key is required outside Development.");

        var keys = new Dictionary<string, RsaSecurityKey>(StringComparer.Ordinal);
        try
        {
            foreach (var item in options.PublicKeys)
            {
                if (string.IsNullOrWhiteSpace(item.KeyId) || string.IsNullOrWhiteSpace(item.PublicKeyPem) ||
                    keys.ContainsKey(item.KeyId))
                    throw new InvalidOperationException("Player assertion public key IDs must be unique and keys must be configured.");
                var rsa = RSA.Create();
                rsa.ImportFromPem(item.PublicKeyPem);
                if (rsa.KeySize < 3072)
                {
                    rsa.Dispose();
                    throw new InvalidOperationException("Player assertion public keys must be at least 3072 bits.");
                }
                keys.Add(item.KeyId, new RsaSecurityKey(rsa) { KeyId = item.KeyId });
            }
            return new PlayerAssertionKeyRing(keys);
        }
        catch
        {
            foreach (var key in keys.Values) key.Rsa?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var key in keys.Values) key.Rsa?.Dispose();
    }
}

public sealed class PlayerAssertionPrincipalValidator(
    UserManager<ApplicationUser> users,
    SaturdayScoutDbContext domain,
    PlayerAssertionApiOptions options)
{
    public async Task<bool> ValidateAsync(ClaimsPrincipal principal, SecurityToken token, CancellationToken ct)
    {
        var identity = principal.Identities.FirstOrDefault(x => x.IsAuthenticated);
        if (identity is null) return false;
        var subjects = identity.FindAll("sub").Select(x => x.Value).ToArray();
        var callers = identity.FindAll(PlayerAssertionDefaults.ClientIdClaim).Select(x => x.Value).ToArray();
        var issuedAtClaims = identity.FindAll("iat").ToArray();
        var tokenIds = identity.FindAll(JwtRegisteredClaimNames.Jti).Select(x => x.Value).ToArray();
        if (subjects.Length != 1 || callers.Length != 1 || callers[0] != options.AuthorizedClientId ||
            issuedAtClaims.Length != 1 || tokenIds.Length != 1 || !Guid.TryParseExact(tokenIds[0], "N", out _) ||
            !Guid.TryParseExact(subjects[0], "D", out var subjectId) ||
            !long.TryParse(issuedAtClaims[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAtSeconds))
            return false;

        DateTimeOffset issuedAt;
        try { issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        var now = DateTimeOffset.UtcNow;
        if (issuedAt > now.AddSeconds(20) || issuedAt < now.AddSeconds(-options.MaximumLifetimeSeconds - 20) ||
            token.ValidTo <= issuedAt.UtcDateTime || token.ValidTo - issuedAt.UtcDateTime > TimeSpan.FromSeconds(options.MaximumLifetimeSeconds))
            return false;

        var user = await users.FindByIdAsync(subjectId.ToString());
        if (user is null || !user.EmailConfirmed || await users.IsLockedOutAsync(user) ||
            await ScoutIdentityServices.IsDisabledAsync(users, user) ||
            !await users.IsInRoleAsync(user, ScoutRoles.Player)) return false;
        var domainUser = await domain.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == subjectId, ct);
        if (domainUser is null) return false;

        foreach (var claim in identity.Claims.Where(x => x.Type == ClaimTypes.Role || x.Type == "role" ||
                     x.Type == ClaimTypes.NameIdentifier || x.Type == ClaimTypes.Name).ToArray())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subjectId.ToString("D")));
        identity.AddClaim(new Claim(ClaimTypes.Name, domainUser.DisplayName));
        identity.AddClaim(new Claim(ClaimTypes.Role, ScoutRoles.Player));
        return true;
    }
}

public static class PlayerAssertionAuthentication
{
    public static IServiceCollection AddPlayerAssertionAuthentication(this IServiceCollection services,
        IConfiguration configuration, bool isDevelopment)
    {
        var options = configuration.GetSection(PlayerAssertionApiOptions.SectionName).Get<PlayerAssertionApiOptions>() ?? new();
        var keyRing = PlayerAssertionKeyRing.Create(options, isDevelopment);
        services.AddSingleton(options);
        services.AddSingleton(keyRing);
        services.AddScoped<PlayerAssertionPrincipalValidator>();
        services.AddAuthentication()
            .AddJwtBearer(PlayerAssertionDefaults.Scheme, jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.IncludeErrorDetails = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(20),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    IssuerSigningKeys = keyRing.All,
                    IssuerSigningKeyResolver = (_, _, keyId, _) => keyRing.Resolve(keyId),
                    NameClaimType = "sub",
                    RoleClaimType = ClaimTypes.Role
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var validator = context.HttpContext.RequestServices.GetRequiredService<PlayerAssertionPrincipalValidator>();
                        if (context.Principal is null || context.SecurityToken is null ||
                            !await validator.ValidateAsync(context.Principal, context.SecurityToken,
                                context.HttpContext.RequestAborted))
                            context.Fail("Invalid Player assertion.");
                    }
                };
            })
            .AddPolicyScheme(PlayerAssertionDefaults.ApiUserScheme, null, policy =>
            {
                policy.ForwardDefaultSelector = context =>
                {
                    if (isDevelopment && context.Request.Headers.ContainsKey("X-Dev-Subject")) return "Development";
                    if (context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        return PlayerAssertionDefaults.Scheme;
                    return IdentityConstants.ApplicationScheme;
                };
            });
        return services;
    }
}
