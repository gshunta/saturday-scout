using System.Security.Cryptography;
using SaturdayScout.Domain;

namespace SaturdayScout.Infrastructure;

internal static class LeagueCreation
{
    public static (League League, LeagueMember CommissionerMembership) Create(
        string name, Guid commissionerUserId, DateTimeOffset now)
    {
        var inviteCodeHash = Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
        var league = new League(Guid.NewGuid(), name, commissionerUserId, inviteCodeHash, now.AddYears(1));
        var membership = new LeagueMember(league.Id, commissionerUserId, MembershipRole.Commissioner, now);
        return (league, membership);
    }
}
