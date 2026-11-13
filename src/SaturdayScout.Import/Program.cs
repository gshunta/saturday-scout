using Microsoft.EntityFrameworkCore;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.CollegeFootballData;
using SaturdayScout.Infrastructure.Persistence;

if (args.Length > 0 && args[0] == "dev-picks-scenario")
    return await DevelopmentPicksScenarioCommand.RunAsync(args[1..]);

try
{
    if (!(args.Length == 6 && args[5] == "--live" || args.Length == 7 && args[5] == "--fixture-dir") ||
        args[0] != "college" || args[1] != "--season" ||
        !int.TryParse(args[2], out var season) || args[3] != "--week" ||
        !int.TryParse(args[4], out var week))
    {
        Console.Error.WriteLine("Usage: college --season YEAR --week WEEK --fixture-dir DIR | college --season YEAR --week WEEK --live");
        return 2;
    }
    var isLive = args[5] == "--live";
    var connection = Environment.GetEnvironmentVariable("SATURDAY_SCOUT_DB_CONNECTION")
        ?? throw new InvalidOperationException("SATURDAY_SCOUT_DB_CONNECTION is required.");
    var options = new DbContextOptionsBuilder<SaturdayScoutDbContext>().UseNpgsql(connection).Options;
    await using var db = new SaturdayScoutDbContext(options);
    if ((await db.Database.GetPendingMigrationsAsync()).Any())
        throw new InvalidOperationException("Apply EF migrations before importing.");

    IFootballWeekProvider provider;
    if (isLive)
    {
        if (Environment.GetEnvironmentVariable("CFBD_LIVE_IMPORT_APPROVED") != "yes")
            throw new InvalidOperationException("Live import requires CFBD_LIVE_IMPORT_APPROVED=yes after explicit approval.");
        var key = Environment.GetEnvironmentVariable("CFBD_API_KEY")
            ?? throw new InvalidOperationException("CFBD_API_KEY is required for live import.");
        if (!int.TryParse(Environment.GetEnvironmentVariable("CFBD_SCOUT_MONTHLY_CAP"), out var ownCap) ||
            !int.TryParse(Environment.GetEnvironmentVariable("CFBD_SHARED_REMAINING"), out var sharedRemaining) ||
            ownCap <= 0 || sharedRemaining <= 0)
            throw new InvalidOperationException("Set positive CFBD_SCOUT_MONTHLY_CAP and CFBD_SHARED_REMAINING values.");
        var client = new HttpClient { BaseAddress = new Uri("https://api.collegefootballdata.com"),
            Timeout = TimeSpan.FromSeconds(15) };
        var providerOptions = new CollegeFootballDataOptions(key, ownCap, sharedRemaining);
        provider = new CollegeFootballDataClient(client, new CfbdRequestBudget(db, providerOptions), providerOptions);
    }
    else
    {
        var directory = args[6];
        provider = new FixtureWeekProvider(directory);
    }
    var importer = new FootballImportService(provider, new FootballImportStore(db));
    var report = await importer.ImportWeekAsync(Sport.CollegeFootball, season, week, CancellationToken.None);
    Console.WriteLine($"Teams: {report.TeamsCreated} created, {report.TeamsUpdated} updated. Games: {report.GamesCreated} created, {report.GamesUpdated} updated, {report.GamesUnchanged} unchanged.");
    foreach (var issue in report.Issues)
        Console.WriteLine($"Skipped/ambiguous {issue.ExternalId ?? "(unknown)"}: {issue.Reason}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

file sealed class FixtureWeekProvider(string directory) : IFootballWeekProvider
{
    public async Task<ImportedWeek> GetWeekAsync(Sport sport, int season, int week, CancellationToken ct)
    {
        var teams = await File.ReadAllTextAsync(Path.Combine(directory, "teams.json"), ct);
        var schedule = await File.ReadAllTextAsync(Path.Combine(directory, "schedule.json"), ct);
        var games = await File.ReadAllTextAsync(Path.Combine(directory, "games.json"), ct);
        var batch = CollegeFootballDataParser.Parse(teams, schedule, games, DateTimeOffset.UtcNow);
        if (batch.Games.Any(x => x.Sport != sport || x.Season != season || x.Week != week))
            throw new InvalidOperationException("Fixture games do not match requested sport, season, and week.");
        return batch;
    }
}
