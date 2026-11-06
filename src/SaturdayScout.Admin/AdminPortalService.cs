using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using SaturdayScout.Application;

namespace SaturdayScout.Admin;

public sealed class AdminPortalService(
    AuthenticationStateProvider authenticationState,
    IAuthorizationService authorization,
    IAdminScoutService operations)
{
    private async Task EnsureAdministratorAsync()
    {
        var user = (await authenticationState.GetAuthenticationStateAsync()).User;
        if (!(await authorization.AuthorizeAsync(user, "SystemAdministrator")).Succeeded)
            throw new UnauthorizedAccessException("Administrator access is required.");
    }

    public async Task<AdminDashboardDto> GetDashboardAsync(CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.GetDashboardAsync(ct); }
    public async Task<AdminGamesPageDto> BrowseGamesAsync(AdminGameQuery query, CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.BrowseGamesAsync(query, ct); }
    public async Task<IReadOnlyList<AdminLeagueDto>> GetLeaguesAsync(CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.GetLeaguesAsync(ct); }
    public async Task<IReadOnlyList<AdminCommissionerDto>> GetEligibleCommissionersAsync(CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.GetEligibleCommissionersAsync(ct); }
    public async Task<AdminLeagueDto> CreateLeagueAsync(CreateAdminLeagueRequest request, CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.CreateLeagueAsync(request, ct); }
    public async Task<IReadOnlyList<AdminSlateSummaryDto>> GetSlatesAsync(CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.GetSlatesAsync(ct); }
    public async Task<AdminSlateDto> GetSlateAsync(Guid id, CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.GetSlateAsync(id, ct); }
    public async Task<AdminSlateDto> CreateSlateAsync(CreateAdminSlateRequest request, CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.CreateSlateAsync(request, ct); }
    public async Task<AdminSlateDto> UpdateSlateGamesAsync(Guid id, UpdateAdminSlateGamesRequest request, CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.UpdateSlateGamesAsync(id, request, ct); }
    public async Task<AdminSlateDto> RandomizeSlateGamesAsync(Guid id, RandomizeAdminSlateRequest request, CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.RandomizeSlateGamesAsync(id, request, ct); }
    public async Task<AdminSlateDto> PublishSlateAsync(Guid id, PublishAdminSlateRequest request, CancellationToken ct = default)
    { await EnsureAdministratorAsync(); return await operations.PublishSlateAsync(id, request, ct); }
}
