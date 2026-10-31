using SaturdayScout.Domain;

namespace SaturdayScout.Application;

public sealed record ImportedTeam(string Provider, string ExternalId, string Name, string? Abbreviation);
public sealed record ImportedGame(string Provider, string ExternalId, Sport Sport, int Season, int Week,
    string HomeTeamExternalId, string AwayTeamExternalId, DateTimeOffset? KickoffUtc,
    GameStatus? Status, string RawStatus, int? HomeScore, int? AwayScore, DateTimeOffset RetrievedUtc);
public sealed record ImportIssue(string? ExternalId, string Reason);
public sealed record ImportedWeek(IReadOnlyList<ImportedTeam> Teams, IReadOnlyList<ImportedGame> Games,
    IReadOnlyList<ImportIssue> Issues);
public sealed record ImportReport(int TeamsCreated, int TeamsUpdated, int GamesCreated, int GamesUpdated,
    int GamesUnchanged, IReadOnlyList<ImportIssue> Issues);

public interface IFootballWeekProvider
{
    Task<ImportedWeek> GetWeekAsync(Sport sport, int season, int week, CancellationToken cancellationToken);
}

public interface IFootballImportStore
{
    Task<ImportReport> ApplyWeekAsync(ImportedWeek week, CancellationToken cancellationToken);
}

public sealed class FootballImportService(IFootballWeekProvider provider, IFootballImportStore store)
{
    public async Task<ImportReport> ImportWeekAsync(Sport sport, int season, int week, CancellationToken cancellationToken)
    {
        if (sport != Sport.CollegeFootball || season is < 1900 or > 9999 || week is < 1 or > 30)
            throw new ArgumentException("Phase 4 supports a valid college football season and week only.");
        var batch = await provider.GetWeekAsync(sport, season, week, cancellationToken);
        return await store.ApplyWeekAsync(batch, cancellationToken);
    }
}
