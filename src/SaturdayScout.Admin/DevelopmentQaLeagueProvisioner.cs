using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Admin;

public sealed record DevelopmentQaLeagueProvisionResult(Guid LeagueId, bool LeagueCreated, bool PlayerMembershipAdded);

/// <summary>
/// Creates the fixed, development-only QA league using the normal league and member services.
/// It never provisions or repairs Identity/domain accounts.
/// </summary>
public sealed class DevelopmentQaLeagueProvisioner(
    SaturdayScoutDbContext domainDb,
    UserManager<ApplicationUser> users,
    IAdminScoutService adminScout,
    IScoutService scout)
{
    public const string LeagueName = "Saturday Scout QA";

    public async Task<DevelopmentQaLeagueProvisionResult> ProvisionAsync(
        string commissionerEmail, string playerEmail, bool operatorApproved, CancellationToken ct)
    {
        if (!operatorApproved)
            throw new InvalidOperationException("Explicit operator approval is required.");

        var commissionerIdentity = await FindConfirmedIdentityUserAsync(commissionerEmail, ct);
        if (!await users.IsInRoleAsync(commissionerIdentity, ScoutRoles.SystemAdministrator))
            throw new InvalidOperationException("The QA league commissioner must be an existing SystemAdministrator account.");
        var commissioner = await domainDb.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == commissionerIdentity.Id, ct);
        if (commissioner is null || commissioner.ExternalSubjectId != $"identity:{commissionerIdentity.Id:N}")
            throw new InvalidOperationException("The commissioner Identity and domain user mapping is incomplete.");

        var playerIdentity = await FindConfirmedIdentityUserAsync(playerEmail, ct);
        if (!await users.IsInRoleAsync(playerIdentity, ScoutRoles.Player) ||
            await users.IsInRoleAsync(playerIdentity, ScoutRoles.SystemAdministrator))
            throw new InvalidOperationException("The selected account must be an existing Player and not a SystemAdministrator.");
        var player = await domainDb.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == playerIdentity.Id, ct);
        if (player is null || player.ExternalSubjectId != $"identity:{playerIdentity.Id:N}")
            throw new InvalidOperationException("The Player Identity and domain user mapping is incomplete.");

        var matchingLeagues = await domainDb.Leagues.AsNoTracking()
            .Where(x => x.Name == LeagueName).ToListAsync(ct);
        if (matchingLeagues.Count > 1)
            throw new InvalidOperationException("Multiple QA leagues already exist; refusing to choose or modify one.");

        var leagueCreated = false;
        Guid leagueId;
        if (matchingLeagues.Count == 1)
        {
            var existing = matchingLeagues[0];
            if (existing.CommissionerUserId != commissioner.Id)
                throw new InvalidOperationException("The existing QA league has a different commissioner; no changes were made.");
            leagueId = existing.Id;
        }
        else
        {
            var created = await adminScout.CreateLeagueAsync(
                new CreateAdminLeagueRequest(LeagueName, commissioner.Id), ct);
            leagueId = created.Id;
            leagueCreated = true;
        }

        if (await domainDb.WeeklySlates.AsNoTracking().AnyAsync(x => x.LeagueId == leagueId, ct))
            throw new InvalidOperationException("The QA league already has a slate; refusing to change it.");

        var commissionerMember = await domainDb.LeagueMembers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.LeagueId == leagueId && x.UserId == commissioner.Id, ct);
        if (commissionerMember?.Role != MembershipRole.Commissioner)
            throw new InvalidOperationException("The QA league commissioner membership is missing or inconsistent.");

        var playerMember = await domainDb.LeagueMembers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.LeagueId == leagueId && x.UserId == player.Id, ct);
        var membershipAdded = false;
        if (playerMember is null)
        {
            await scout.AddMemberAsync(commissioner.Id, leagueId,
                new AddMemberRequest(player.ExternalSubjectId), ct);
            membershipAdded = true;
        }
        else if (playerMember.Role != MembershipRole.Member)
        {
            throw new InvalidOperationException("Player 3 has a non-Player membership in the QA league; refusing to change it.");
        }

        return new DevelopmentQaLeagueProvisionResult(leagueId, leagueCreated, membershipAdded);
    }

    private async Task<ApplicationUser> FindConfirmedIdentityUserAsync(string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 256)
            throw new ArgumentException("A valid existing account email is required.", nameof(email));
        var identity = await users.FindByEmailAsync(email.Trim())
            ?? throw new InvalidOperationException("The requested Identity account does not exist.");
        if (!identity.EmailConfirmed || !string.Equals(identity.Email, email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested Identity account is not email-confirmed or does not match.");
        ct.ThrowIfCancellationRequested();
        return identity;
    }
}
