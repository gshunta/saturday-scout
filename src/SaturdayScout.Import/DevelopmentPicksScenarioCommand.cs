using Microsoft.EntityFrameworkCore;
using Npgsql;
using SaturdayScout.Infrastructure.Development;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;

internal static class DevelopmentPicksScenarioCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.Ordinal))
            return Fail("This command is available only when ASPNETCORE_ENVIRONMENT=Development.");
        if (args.Length != 7 || args[0] != "--apply")
            return Fail("Usage: dev-picks-scenario --apply --league-id GUID --player-id GUID --scenario-key KEY");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !values.TryAdd(args[index], args[index + 1]))
                return Fail("Scenario options are invalid or repeated.");
        }
        if (values.Count != 3 || !values.TryGetValue("--league-id", out var leagueText) ||
            !Guid.TryParse(leagueText, out var leagueId) ||
            !values.TryGetValue("--player-id", out var playerText) ||
            !Guid.TryParse(playerText, out var playerId) ||
            !values.TryGetValue("--scenario-key", out var scenarioKey))
            return Fail("Supply --league-id, --player-id, and --scenario-key.");

        var connection = Environment.GetEnvironmentVariable("SATURDAY_SCOUT_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            return Fail("SATURDAY_SCOUT_DB_CONNECTION is required and must target local development PostgreSQL.");

        NpgsqlConnectionStringBuilder target;
        try { target = new NpgsqlConnectionStringBuilder(connection); }
        catch (ArgumentException) { return Fail("SATURDAY_SCOUT_DB_CONNECTION is invalid."); }
        if (!string.Equals(target.Database, "saturday_scout", StringComparison.Ordinal) ||
            target.Port != 55432 ||
            target.Host is not ("127.0.0.1" or "localhost" or "::1"))
            return Fail("Refusing to write: the target must be saturday_scout on local loopback port 55432.");

        var domainOptions = new DbContextOptionsBuilder<SaturdayScoutDbContext>()
            .UseNpgsql(connection).Options;
        var identityOptions = new DbContextOptionsBuilder<ScoutIdentityDbContext>()
            .UseNpgsql(connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "auth"))
            .Options;

        try
        {
            await using var domain = new SaturdayScoutDbContext(domainOptions);
            await using var identity = new ScoutIdentityDbContext(identityOptions);
            if ((await domain.Database.GetPendingMigrationsAsync()).Any() ||
                (await identity.Database.GetPendingMigrationsAsync()).Any())
                return Fail("Database migrations are pending. Apply them separately before running this command.");

            var result = await new DevelopmentPicksScenarioProvisioner(domain, identity)
                .ProvisionAsync(leagueId, playerId, scenarioKey);
            Console.WriteLine(result.ReusedExistingSlate
                ? "Reused the matching active development scenario without changing it."
                : "Created and published the development picks scenario.");
            Console.WriteLine($"League ID: {result.LeagueId:D}");
            Console.WriteLine($"Player ID: {result.PlayerId:D}");
            Console.WriteLine($"Slate ID: {result.SlateId:D}");
            Console.WriteLine($"Season/week: {result.Season}/{result.Week}");
            Console.WriteLine($"Lock UTC: {result.LockUtc:O}");
            Console.WriteLine($"Games: 10; synthetic: {result.UsesSyntheticGames}");
            Console.WriteLine("No player account, membership, or pick sheet was created or changed.");
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            return Fail(exception.Message);
        }
        catch (Exception exception)
        {
            return Fail($"Development scenario provisioning failed ({exception.GetType().Name}). No credentials or database details were printed.");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
