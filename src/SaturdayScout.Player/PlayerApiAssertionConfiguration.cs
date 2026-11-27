namespace SaturdayScout.Player;

public static class PlayerApiAssertionConfiguration
{
    public static void ValidateProduction(PlayerApiAssertionOptions options, string? apiOriginText)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCommon(options, apiOriginText);

        // Production has exactly one key source. Never fall back to a local path when the
        // Key Vault reference is missing or unresolved.
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
            throw new InvalidOperationException("Production Player signing keys must be supplied through ApiAssertion:PrivateKeyPem.");

        using var validatedKey = PlayerApiSigningKey.LoadFromPem(options.PrivateKeyPem);
    }

    public static void ValidateTesting(PlayerApiAssertionOptions options, string? apiOriginText)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCommon(options, apiOriginText);
        if (!Path.IsPathFullyQualified(options.PrivateKeyPath) || !File.Exists(options.PrivateKeyPath) ||
            !string.IsNullOrWhiteSpace(options.PrivateKeyPem))
            throw new InvalidOperationException("Testing Player assertion configuration requires the existing private-key file setting.");

        PlayerApiAssertionIssuer.ValidatePrivateKeyFile(options.PrivateKeyPath);
    }

    private static void ValidateCommon(PlayerApiAssertionOptions options, string? apiOriginText)
    {
        if (!Uri.TryCreate(apiOriginText, UriKind.Absolute, out var apiOrigin) || apiOrigin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(apiOrigin.UserInfo) || apiOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(apiOrigin.Query) || !string.IsNullOrEmpty(apiOrigin.Fragment))
            throw new InvalidOperationException("Player:ApiOrigin must be an HTTPS origin outside Development.");

        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience) ||
            string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.KeyId) ||
            options.TokenLifetimeSeconds is < 30 or > 180)
            throw new InvalidOperationException("Player API assertion signing configuration is incomplete or invalid.");
    }
}
