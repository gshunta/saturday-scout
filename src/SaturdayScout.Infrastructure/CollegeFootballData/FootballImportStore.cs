using Microsoft.EntityFrameworkCore;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure.CollegeFootballData;

public sealed class FootballImportStore(SaturdayScoutDbContext db) : IFootballImportStore
{
    public async Task<ImportReport> ApplyWeekAsync(ImportedWeek week, CancellationToken ct)
    {
        var issues = week.Issues.ToList();
        var teamsCreated = 0;
        var teamsUpdated = 0;
        var gamesCreated = 0;
        var gamesUpdated = 0;
        var gamesUnchanged = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(789352104)", ct);

        var existingTeams = await db.Teams.Where(x => x.Sport == Sport.CollegeFootball &&
            x.Provider == CollegeFootballDataParser.ProviderName).ToDictionaryAsync(x => x.ExternalId, ct);
        foreach (var row in week.Teams)
        {
            if (row.Provider != CollegeFootballDataParser.ProviderName || string.IsNullOrWhiteSpace(row.ExternalId) ||
                string.IsNullOrWhiteSpace(row.Name) || row.Name.Length > 200 ||
                string.IsNullOrWhiteSpace(row.Abbreviation) || row.Abbreviation.Length > 20)
            {
                issues.Add(new ImportIssue(row.ExternalId, "Invalid team identity or metadata; team skipped."));
                continue;
            }
            if (existingTeams.TryGetValue(row.ExternalId, out var team))
            {
                if (team.Name != row.Name || team.Abbreviation != row.Abbreviation)
                {
                    team.UpdateDetails(row.Name, row.Abbreviation);
                    teamsUpdated++;
                }
            }
            else
            {
                team = new Team(Guid.NewGuid(), Sport.CollegeFootball, row.Name, row.Abbreviation,
                    row.Provider, row.ExternalId);
                db.Teams.Add(team);
                existingTeams.Add(row.ExternalId, team);
                teamsCreated++;
            }
        }

        var existingGames = await db.Games.Where(x => x.Provider == CollegeFootballDataParser.ProviderName)
            .ToDictionaryAsync(x => x.ExternalId, ct);
        foreach (var row in week.Games)
        {
            if (row.Provider != CollegeFootballDataParser.ProviderName || row.Sport != Sport.CollegeFootball ||
                string.IsNullOrWhiteSpace(row.ExternalId) || row.KickoffUtc is null ||
                row.KickoffUtc.Value.Offset != TimeSpan.Zero || row.RetrievedUtc.Offset != TimeSpan.Zero ||
                row.RawStatus.Length > 64 || row.Season is < 1900 or > 9999 || row.Week is < 1 or > 30 ||
                row.Status is null || !existingTeams.TryGetValue(row.HomeTeamExternalId, out var home) ||
                !existingTeams.TryGetValue(row.AwayTeamExternalId, out var away) || home.Id == away.Id)
            {
                issues.Add(new ImportIssue(row.ExternalId, "Incomplete or invalid game identity, kickoff, status, or team; game skipped."));
                continue;
            }
            if (row.Status == GameStatus.Final && (row.HomeScore is null or < 0 || row.AwayScore is null or < 0))
            {
                issues.Add(new ImportIssue(row.ExternalId, "Final status lacks valid scores; game skipped."));
                continue;
            }
            if (row.Status != GameStatus.Final && (row.HomeScore is not null || row.AwayScore is not null))
            {
                issues.Add(new ImportIssue(row.ExternalId, "Non-final game has scores; game skipped."));
                continue;
            }
            if (!existingGames.TryGetValue(row.ExternalId, out var game))
            {
                game = new Game(Guid.NewGuid(), row.Sport, row.Season, row.Week, home.Id, away.Id,
                    row.KickoffUtc.Value, row.Provider, row.ExternalId);
                if (row.Status == GameStatus.Final) game.SetFinalScore(row.HomeScore!.Value, row.AwayScore!.Value);
                else if (row.Status != GameStatus.Scheduled) game.SetStatus(row.Status.Value);
                game.RecordSource(row.RawStatus, row.RetrievedUtc);
                db.Games.Add(game);
                existingGames.Add(row.ExternalId, game);
                gamesCreated++;
                continue;
            }
            if (game.Sport != row.Sport || game.Season != row.Season || game.Week != row.Week ||
                game.HomeTeamId != home.Id || game.AwayTeamId != away.Id)
            {
                issues.Add(new ImportIssue(row.ExternalId, "Existing game identity or participants conflict; update skipped."));
                continue;
            }
            if (game.SourceRetrievedUtc is not null && row.RetrievedUtc < game.SourceRetrievedUtc)
            {
                issues.Add(new ImportIssue(row.ExternalId, "Older provider snapshot; update skipped."));
                continue;
            }
            if (game.Status == GameStatus.Final)
            {
                if (row.Status != GameStatus.Final || game.HomeScore != row.HomeScore || game.AwayScore != row.AwayScore)
                {
                    issues.Add(new ImportIssue(row.ExternalId, "Existing final result differs from source; update skipped pending manual review."));
                    continue;
                }
            }
            else if (game.Status is GameStatus.Canceled or GameStatus.NoContest)
            {
                issues.Add(new ImportIssue(row.ExternalId, "Concluded void game requires manual review; update skipped."));
                continue;
            }
            else if (game.Status == GameStatus.InProgress && row.Status != GameStatus.InProgress && row.Status != GameStatus.Final)
            {
                issues.Add(new ImportIssue(row.ExternalId, "Provider snapshot would regress an in-progress game; update skipped."));
                continue;
            }
            var changed = false;
            if (game.KickoffUtc != row.KickoffUtc && game.Status != GameStatus.Final)
            {
                var violatesLock = await db.SlateGames.AnyAsync(sg => sg.GameId == game.Id &&
                    db.WeeklySlates.Any(s => s.Id == sg.SlateId && s.Status != SlateStatus.Draft &&
                        s.LockUtc > row.KickoffUtc), ct);
                if (violatesLock)
                    issues.Add(new ImportIssue(row.ExternalId, "Kickoff change would precede a published slate lock; kickoff retained."));
                else
                {
                    game.SetKickoff(row.KickoffUtc.Value);
                    changed = true;
                }
            }
            if (game.Status != row.Status)
            {
                if (row.Status == GameStatus.Final) game.SetFinalScore(row.HomeScore!.Value, row.AwayScore!.Value);
                else game.SetStatus(row.Status.Value);
                changed = true;
            }
            if (game.SourceStatus != row.RawStatus || game.SourceRetrievedUtc != row.RetrievedUtc)
            {
                game.RecordSource(row.RawStatus, row.RetrievedUtc);
                changed = true;
            }
            if (changed) gamesUpdated++;
            else gamesUnchanged++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportReport(teamsCreated, teamsUpdated, gamesCreated, gamesUpdated, gamesUnchanged, issues);
    }
}
