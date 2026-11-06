using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure.Identity;

public sealed class SystemAdministratorMfaRequirement : IAuthorizationRequirement;
public sealed class MfaEnrollmentRequirement : IAuthorizationRequirement;

public sealed class SystemAdministratorMfaHandler(IServiceScopeFactory scopes)
    : AuthorizationHandler<SystemAdministratorMfaRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        SystemAdministratorMfaRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
            !context.User.HasClaim(ScoutIdentityServices.MfaClaim, "mfa") ||
            !context.User.IsInRole(ScoutRoles.SystemAdministrator)) return;
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var domain = scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>();
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null || !user.EmailConfirmed || !await users.GetTwoFactorEnabledAsync(user) ||
            await users.IsLockedOutAsync(user) || await ScoutIdentityServices.IsDisabledAsync(users, user) ||
            !await users.IsInRoleAsync(user, ScoutRoles.SystemAdministrator) ||
            !await domain.Users.AsNoTracking().AnyAsync(x => x.Id == id)) return;
        context.Succeed(requirement);
    }
}

public sealed class MfaEnrollmentHandler(IServiceScopeFactory scopes)
    : AuthorizationHandler<MfaEnrollmentRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        MfaEnrollmentRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
            !context.User.HasClaim(ScoutIdentityServices.MfaClaim, "pwd") ||
            !ScoutIdentityServices.RecentlyAuthenticated(context.User, TimeSpan.FromMinutes(10))) return;
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var domain = scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>();
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null || !user.EmailConfirmed || await users.GetTwoFactorEnabledAsync(user) ||
            await users.IsLockedOutAsync(user) || await ScoutIdentityServices.IsDisabledAsync(users, user) ||
            !await users.IsInRoleAsync(user, ScoutRoles.SystemAdministrator) ||
            !await domain.Users.AsNoTracking().AnyAsync(x => x.Id == id)) return;
        context.Succeed(requirement);
    }
}
