using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Api;

public sealed class LeagueCommissionerRequirement : IAuthorizationRequirement;

public sealed class LeagueCommissionerAuthorizationHandler(SaturdayScoutDbContext db)
    : AuthorizationHandler<LeagueCommissionerRequirement, Guid>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        LeagueCommissionerRequirement requirement, Guid leagueId)
    {
        if (leagueId == Guid.Empty ||
            !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return;
        if (await db.LeagueMembers.AsNoTracking().AnyAsync(x => x.LeagueId == leagueId &&
            x.UserId == userId && x.Role == MembershipRole.Commissioner))
            context.Succeed(requirement);
    }
}
