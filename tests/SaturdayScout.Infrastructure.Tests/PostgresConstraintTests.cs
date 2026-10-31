using Microsoft.EntityFrameworkCore;
using Npgsql;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Persistence;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class PostgresConstraintTests
{
    [PostgresFact]
    public async Task MigrationRoundTripAndDatabaseConstraints()
    {
        var adminConnection = Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION")!;

        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnection);
        adminBuilder.Database = "postgres";
        var databaseName = $"saturday_scout_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databaseName, Pooling = false };
            var options = new DbContextOptionsBuilder<SaturdayScoutDbContext>()
                .UseNpgsql(testBuilder.ConnectionString).Options;
            await using var db = new SaturdayScoutDbContext(options);
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();

            var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            var user = new User(Guid.NewGuid(), "Test user", Guid.NewGuid().ToString("N"));
            var league = new League(Guid.NewGuid(), "Test league", user.Id, "hashed-invite", now.AddDays(1));
            var member = new LeagueMember(league.Id, user.Id, MembershipRole.Commissioner, now);
            var home = new Team(Guid.NewGuid(), Sport.Nfl, "Home", "HOM", "fixture", "home");
            var away = new Team(Guid.NewGuid(), Sport.Nfl, "Away", "AWY", "fixture", "away");
            var games = Enumerable.Range(0, 10).Select(i => new Game(Guid.NewGuid(), Sport.Nfl, 2026, 8,
                home.Id, away.Id, now.AddHours(4 + i), "fixture", $"game-{i}")).ToList();
            var extraGame = new Game(Guid.NewGuid(), Sport.Nfl, 2026, 8,
                home.Id, away.Id, now.AddHours(20), "fixture", "extra-game");
            var slate = new WeeklySlate(Guid.NewGuid(), league.Id, Sport.Nfl, 2026, 8);
            slate.ReplaceGames(games);
            slate.Publish(games, now.AddHours(3), now);
            var sheet = new PickSheet(Guid.NewGuid(), slate.Id, user.Id);
            sheet.ReplaceDraftPicks(slate, games, games.Select((g, i) => (g.Id, home.Id, i + 1)), now.AddMinutes(1));

            db.AddRange(user, league, member, home, away);
            db.AddRange(games);
            db.Add(extraGame);
            db.Add(slate);
            db.Add(sheet);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var loaded = await db.PickSheets.Include(x => x.Picks).SingleAsync(x => x.Id == sheet.Id);
            var loadedSlate = await db.WeeklySlates.Include(x => x.Games).SingleAsync(x => x.Id == slate.Id);
            Assert.Equal(10, loaded.Picks.Count);
            Assert.Equal(10, loadedSlate.Games.Count);
            Assert.Equal(1, loaded.Version);

            var first = games[0];
            var rankError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"Picks\" (\"SheetId\", \"GameId\", \"SelectedTeamId\", \"ConfidenceRank\") VALUES ({sheet.Id}, {extraGame.Id}, {home.Id}, {2})"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, rankError.SqlState);

            var duplicateGameError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"Picks\" (\"SheetId\", \"GameId\", \"SelectedTeamId\", \"ConfidenceRank\") VALUES ({sheet.Id}, {first.Id}, {home.Id}, {1})"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateGameError.SqlState);

            var rankCheckError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"Picks\" (\"SheetId\", \"GameId\", \"SelectedTeamId\", \"ConfidenceRank\") VALUES ({sheet.Id}, {extraGame.Id}, {home.Id}, {11})"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rankCheckError.SqlState);

            await using var staleDb = new SaturdayScoutDbContext(options);
            var staleSheet = await staleDb.PickSheets.Include(x => x.Picks).SingleAsync(x => x.Id == sheet.Id);
            var staleSlate = await staleDb.WeeklySlates.Include(x => x.Games).SingleAsync(x => x.Id == slate.Id);
            loaded.Submit(loadedSlate, now.AddMinutes(2));
            await db.SaveChangesAsync();
            staleSheet.Submit(staleSlate, now.AddMinutes(3));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SATURDAY_SCOUT_TEST_ADMIN_CONNECTION")))
            Skip = "Set SATURDAY_SCOUT_TEST_ADMIN_CONNECTION to run the PostgreSQL integration test.";
    }
}
