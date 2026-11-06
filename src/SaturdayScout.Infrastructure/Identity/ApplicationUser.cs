using Microsoft.AspNetCore.Identity;

namespace SaturdayScout.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
}

public static class ScoutRoles
{
    public const string SystemAdministrator = nameof(SystemAdministrator);
    public const string LeagueCommissioner = nameof(LeagueCommissioner);
    public const string Player = nameof(Player);

    public static readonly string[] All = [SystemAdministrator, LeagueCommissioner, Player];
}

public sealed class AccountInvitation
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Role { get; set; } = ScoutRoles.Player;
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? RedeemedUtc { get; set; }
    public Guid? IssuedByUserId { get; set; }
}
