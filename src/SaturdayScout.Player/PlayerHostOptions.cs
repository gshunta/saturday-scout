namespace SaturdayScout.Player;

public sealed class PlayerHostOptions
{
    public const string SectionName = "Player";
    public string PublicOrigin { get; set; } = "";
    public string ApiOrigin { get; set; } = "";
}

public sealed class PlayerCookieOptions
{
    public const string SectionName = "PlayerCookie";
    public string Name { get; set; } = "__Host-SaturdayScout.Player";
    public int IdleTimeoutMinutes { get; set; } = 480;
    public int AbsoluteTimeoutHours { get; set; } = 24;
}

public sealed class PlayerApiAssertionOptions
{
    public const string SectionName = "ApiAssertion";
    public string Issuer { get; set; } = "SaturdayScout.Player";
    public string Audience { get; set; } = "SaturdayScout.Api";
    public string ClientId { get; set; } = "saturday-scout-player";
    public string KeyId { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public int TokenLifetimeSeconds { get; set; } = 120;
}
