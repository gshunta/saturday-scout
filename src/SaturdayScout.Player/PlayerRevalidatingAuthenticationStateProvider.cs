using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Player;

public sealed class PlayerRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        if (authenticationState.User.Identity?.IsAuthenticated != true ||
            !ScoutIdentityServices.RecentlyAuthenticated(authenticationState.User, TimeSpan.FromHours(24))) return false;

        await using var scope = scopes.CreateAsyncScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await signIn.ValidateSecurityStampAsync(authenticationState.User);
        if (user is null || !user.EmailConfirmed) return false;

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.IsLockedOutAsync(user) || await ScoutIdentityServices.IsDisabledAsync(users, user) ||
            !await users.IsInRoleAsync(user, ScoutRoles.Player)) return false;

        var domain = scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>();
        return await domain.Users.AsNoTracking().AnyAsync(x => x.Id == user.Id, cancellationToken);
    }
}
