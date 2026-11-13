using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Development;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class DevelopmentPicksScenarioProvisionerTests
{
    [PostgresFact]
    public async Task CreatesTenGamePublishedScenarioAndIsIdempotent()
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION")) { Database = "postgres" };
        var databaseName = $"saturday_scout_devscenario_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var connection = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            { Database = databaseName, Pooling = false }.ConnectionString;
            var domainOptions = new DbContextOptionsBuilder<SaturdayScoutDbContext>().UseNpgsql(connection).Options;
            var identityOptions = new DbContextOptionsBuilder<ScoutIdentityDbContext>()
                .UseNpgsql(connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "auth"))
                .Options;
            await using var domain = new SaturdayScoutDbContext(domainOptions);
            await domain.Database.MigrateAsync();
            await using var identity = new ScoutIdentityDbContext(identityOptions);
            await identity.Database.MigrateAsync();

            var playerId = Guid.NewGuid();
            var leagueId = Guid.NewGuid();
            domain.Add(new User(playerId, "Scenario Player", "dev-scenario-player"));
            domain.Add(new League(leagueId, "Scenario League", playerId, "invite-hash",
                DateTimeOffset.UtcNow.AddDays(30)));
            domain.Add(new LeagueMember(leagueId, playerId, MembershipRole.Member, DateTimeOffset.UtcNow));
            domain.AddRange(Enumerable.Range(0, 20).Select(index => new Team(Guid.NewGuid(),
                Sport.CollegeFootball, $"Team {index:D2}", $"T{index:D2}", "fixture", $"scenario-team-{index:D2}")));
            await domain.SaveChangesAsync();

            var player = new ApplicationUser
            {
                Id = playerId,
                UserName = "scenario-player@example.test",
                Email = "scenario-player@example.test",
                EmailConfirmed = true
            };
            identity.Users.Add(player);
            identity.UserRoles.Add(new IdentityUserRole<Guid>
            {
                UserId = playerId,
                RoleId = await identity.Roles.Where(role => role.Name == ScoutRoles.Player)
                    .Select(role => role.Id).SingleAsync()
            });
            await identity.SaveChangesAsync();

            var provisioner = new DevelopmentPicksScenarioProvisioner(domain, identity);
            var first = await provisioner.ProvisionAsync(leagueId, playerId, "react-e2e-test");
            var repeated = await provisioner.ProvisionAsync(leagueId, playerId, "react-e2e-test");

            Assert.True(first.UsesSyntheticGames);
            Assert.False(first.ReusedExistingSlate);
            Assert.True(repeated.ReusedExistingSlate);
            Assert.Equal(first.SlateId, repeated.SlateId);
            Assert.Equal(first.LockUtc, repeated.LockUtc);
            Assert.True(first.LockUtc >= DateTimeOffset.UtcNow.AddHours(47));
            Assert.True(first.Week > 0);

            await using var verify = new SaturdayScoutDbContext(domainOptions);
            var slate = await verify.WeeklySlates.Include(x => x.Games).SingleAsync(x => x.Id == first.SlateId);
            Assert.Equal(SlateStatus.Published, slate.Status);
            Assert.Equal(10, slate.Games.Count);
            Assert.Equal(10, slate.Games.Select(x => x.GameId).Distinct().Count());
            Assert.Equal(10, await verify.Games.CountAsync(x => x.Provider == "saturday-scout-dev"));
            Assert.False(await verify.PickSheets.AnyAsync(x => x.SlateId == first.SlateId));
            Assert.Equal(1, await verify.WeeklySlates.CountAsync(x => x.LeagueId == leagueId));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
