using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure.Identity;

public sealed class AccountProvisioningService(
    SaturdayScoutDbContext domainDb,
    ScoutIdentityDbContext identityDb,
    UserManager<ApplicationUser> users)
{
    private const string BootstrapMarkerClaim = "saturday_scout:bootstrap_admin";

    public async Task<Guid> BootstrapFirstAdministratorAsync(string email, string displayName,
        string password, bool operatorApproved, CancellationToken ct)
    {
        if (!operatorApproved) throw new InvalidOperationException("Explicit operator approval is required.");
        if (string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
            string.IsNullOrWhiteSpace(displayName) || displayName.Length > 120 ||
            string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Valid email, display name, and password are required.");

        identityDb.Database.SetDbConnection(domainDb.Database.GetDbConnection(), contextOwnsConnection: false);
        await using var tx = await domainDb.Database.BeginTransactionAsync(ct);
        await domainDb.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(789352106)", ct);
        await identityDb.Database.UseTransactionAsync(tx.GetDbTransaction(), ct);
        try
        {
            var role = await identityDb.Roles.SingleAsync(x => x.Name == ScoutRoles.SystemAdministrator, ct);
            if (await identityDb.UserRoles.AnyAsync(x => x.RoleId == role.Id, ct) ||
                await identityDb.UserClaims.AnyAsync(x => x.ClaimType == BootstrapMarkerClaim &&
                    x.ClaimValue == "true", ct))
                throw new InvalidOperationException("The first administrator has already been provisioned.");
            if (await users.FindByEmailAsync(email) is not null)
                throw new InvalidOperationException("An account already uses this email.");

            var id = Guid.NewGuid();
            domainDb.Users.Add(new User(id, displayName.Trim(), $"identity:{id:N}", email.Trim()));
            await domainDb.SaveChangesAsync(ct);
            var identityUser = new ApplicationUser
            {
                Id = id, UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = true
            };
            var created = await users.CreateAsync(identityUser, password);
            if (!created.Succeeded)
                throw new InvalidOperationException("Administrator account creation failed: " +
                    string.Join(", ", created.Errors.Select(x => x.Code)));
            foreach (var roleName in new[] { ScoutRoles.Player, ScoutRoles.SystemAdministrator })
            {
                var assigned = await users.AddToRoleAsync(identityUser, roleName);
                if (!assigned.Succeeded) throw new InvalidOperationException("Administrator role assignment failed.");
            }
            var marked = await users.AddClaimAsync(identityUser,
                new System.Security.Claims.Claim(BootstrapMarkerClaim, "true"));
            if (!marked.Succeeded) throw new InvalidOperationException("Administrator bootstrap marker failed.");
            await tx.CommitAsync(ct);
            return id;
        }
        finally
        {
            await identityDb.Database.UseTransactionAsync(null, ct);
        }
    }

    public async Task<Guid> AcceptInvitationAsync(
        string token, string email, string displayName, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(displayName) || displayName.Length > 120 ||
            string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Invitation, email, display name, and password are required.");

        // Both contexts must use the same connection and transaction. Two independent
        // connections would not make account creation atomic in PostgreSQL.
        identityDb.Database.SetDbConnection(domainDb.Database.GetDbConnection(), contextOwnsConnection: false);
        await using var tx = await domainDb.Database.BeginTransactionAsync(ct);
        await identityDb.Database.UseTransactionAsync(tx.GetDbTransaction(), ct);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            var invitation = await identityDb.Invitations
                .FromSqlInterpolated($"SELECT * FROM auth.\"AccountInvitations\" WHERE \"TokenHash\" = {hash} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (invitation is null || invitation.RedeemedUtc is not null || invitation.ExpiresUtc <= DateTimeOffset.UtcNow ||
                invitation.NormalizedEmail != users.NormalizeEmail(email) || !ScoutRoles.All.Contains(invitation.Role))
                throw new InvalidOperationException("Invitation is invalid or expired.");

            var id = Guid.NewGuid();
            var domainUser = new User(id, displayName.Trim(), $"identity:{id:N}", invitation.Email);
            domainDb.Users.Add(domainUser);
            await domainDb.SaveChangesAsync(ct);

            var identityUser = new ApplicationUser { Id = id, UserName = invitation.Email, Email = invitation.Email };
            var created = await users.CreateAsync(identityUser, password);
            if (!created.Succeeded)
                throw new InvalidOperationException("Identity account creation failed: " +
                    string.Join(", ", created.Errors.Select(x => x.Code)));
            var assigned = await users.AddToRoleAsync(identityUser, ScoutRoles.Player);
            if (!assigned.Succeeded) throw new InvalidOperationException("Player role assignment failed.");
            if (invitation.Role != ScoutRoles.Player)
            {
                assigned = await users.AddToRoleAsync(identityUser, invitation.Role);
                if (!assigned.Succeeded) throw new InvalidOperationException("Invited role assignment failed.");
            }
            invitation.RedeemedUtc = DateTimeOffset.UtcNow;
            await identityDb.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return id;
        }
        finally
        {
            await identityDb.Database.UseTransactionAsync(null, ct);
        }
    }
}
