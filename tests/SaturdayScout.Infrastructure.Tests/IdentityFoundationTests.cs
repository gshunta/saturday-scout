using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class IdentityFoundationTests
{
    [Fact]
    public void IdentityUserRequiresMatchingDomainUserGuid()
    {
        using var db = new ScoutIdentityDbContext(new DbContextOptionsBuilder<ScoutIdentityDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused;Password=unused").Options);
        var entity = db.Model.FindEntityType(typeof(ApplicationUser))!;
        var fk = Assert.Single(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(User));
        Assert.Equal(nameof(ApplicationUser.Id), Assert.Single(fk.Properties).Name);
        Assert.Equal(nameof(User.Id), Assert.Single(fk.PrincipalKey.Properties).Name);
        Assert.True(fk.IsUnique);
        Assert.True(fk.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.Equal("auth", entity.GetSchema());
    }

    [PostgresFact]
    public async Task InvitationCreatesLinkedAccountAndCannotBeReused()
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION")) { Database = "postgres" };
        var name = $"saturday_scout_identity_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            { Database = name, Pooling = false }.ConnectionString;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<SaturdayScoutDbContext>(x => x.UseNpgsql(connection));
            services.AddDbContext<ScoutIdentityDbContext>(x => x.UseNpgsql(connection,
                n => n.MigrationsHistoryTable("__EFMigrationsHistory", "auth")));
            services.AddIdentityCore<ApplicationUser>(x =>
            {
                x.User.RequireUniqueEmail = true;
                x.Password.RequiredLength = 12;
            }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<ScoutIdentityDbContext>();
            services.AddScoped<AccountProvisioningService>();
            await using var provider = services.BuildServiceProvider();
            await using (var setup = provider.CreateAsyncScope())
            {
                await setup.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>().Database.MigrateAsync();
                await setup.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>().Database.MigrateAsync();
                var token = "test-invitation-token";
                setup.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>().Invitations.Add(new AccountInvitation
                {
                    Id = Guid.NewGuid(), Email = "player@example.test", NormalizedEmail = "PLAYER@EXAMPLE.TEST",
                    TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
                    Role = ScoutRoles.Player, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
                });
                await setup.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>().SaveChangesAsync();
            }
            await using (var scope = provider.CreateAsyncScope())
            {
                var id = await scope.ServiceProvider.GetRequiredService<AccountProvisioningService>()
                    .AcceptInvitationAsync("test-invitation-token", "player@example.test", "Player", "Long-test-password-123!", default);
                var domain = await scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>()
                    .Users.SingleAsync(x => x.Id == id);
                var identity = await scope.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>()
                    .Users.SingleAsync(x => x.Id == id);
                Assert.Equal(domain.Id, identity.Id);
                Assert.False(identity.EmailConfirmed);
                Assert.True(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
                    .IsInRoleAsync(identity, ScoutRoles.Player));
            }
            await using (var scope = provider.CreateAsyncScope())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
                    .GetRequiredService<AccountProvisioningService>()
                    .AcceptInvitationAsync("test-invitation-token", "player@example.test", "Player", "Long-test-password-123!", default));
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>().Users.CountAsync());
            }
            const string duplicateToken = "duplicate-email-invitation";
            await using (var scope = provider.CreateAsyncScope())
            {
                var identityDb = scope.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>();
                identityDb.Invitations.Add(new AccountInvitation
                {
                    Id = Guid.NewGuid(), Email = "player@example.test", NormalizedEmail = "PLAYER@EXAMPLE.TEST",
                    TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(duplicateToken))),
                    Role = ScoutRoles.Player, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
                });
                await identityDb.SaveChangesAsync();
            }
            await using (var scope = provider.CreateAsyncScope())
            {
                var provision = scope.ServiceProvider.GetRequiredService<AccountProvisioningService>();
                await Assert.ThrowsAsync<InvalidOperationException>(() => provision
                    .AcceptInvitationAsync("invalid-token", "player@example.test", "Another", "Long-test-password-123!", default));
            }
            await using (var scope = provider.CreateAsyncScope())
            {
                var provision = scope.ServiceProvider.GetRequiredService<AccountProvisioningService>();
                await Assert.ThrowsAsync<InvalidOperationException>(() => provision
                    .AcceptInvitationAsync(duplicateToken, "wrong@example.test", "Another", "Long-test-password-123!", default));
            }
            await using (var scope = provider.CreateAsyncScope())
            {
                var provision = scope.ServiceProvider.GetRequiredService<AccountProvisioningService>();
                await Assert.ThrowsAsync<InvalidOperationException>(() => provision
                    .AcceptInvitationAsync(duplicateToken, "player@example.test", "Another", "Long-test-password-123!", default));
            }
            await using (var scope = provider.CreateAsyncScope())
            {
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>().Users.CountAsync());
                var identityDb = scope.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>();
                Assert.Equal(1, await identityDb.Users.CountAsync());
                Assert.Null((await identityDb.Invitations.SingleAsync(x => x.Email == "player@example.test" &&
                    x.RedeemedUtc == null)).RedeemedUtc);
            }
            const string rollbackToken = "missing-role-invitation";
            await using (var scope = provider.CreateAsyncScope())
            {
                var identityDb = scope.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>();
                identityDb.Roles.Remove(await identityDb.Roles.SingleAsync(x => x.Name == ScoutRoles.SystemAdministrator));
                identityDb.Invitations.Add(new AccountInvitation
                {
                    Id = Guid.NewGuid(), Email = "rollback@example.test", NormalizedEmail = "ROLLBACK@EXAMPLE.TEST",
                    TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rollbackToken))),
                    Role = ScoutRoles.SystemAdministrator, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
                });
                await identityDb.SaveChangesAsync();
            }
            await using (var scope = provider.CreateAsyncScope())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider
                    .GetRequiredService<AccountProvisioningService>()
                    .AcceptInvitationAsync(rollbackToken, "rollback@example.test", "Rollback", "Long-test-password-123!", default));
            }
            await using (var scope = provider.CreateAsyncScope())
            {
                Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SaturdayScoutDbContext>().Users.CountAsync());
                var identityDb = scope.ServiceProvider.GetRequiredService<ScoutIdentityDbContext>();
                Assert.Equal(1, await identityDb.Users.CountAsync());
                Assert.Equal(1, await identityDb.UserRoles.CountAsync());
                Assert.Null((await identityDb.Invitations.SingleAsync(x => x.Email == "rollback@example.test")).RedeemedUtc);
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
