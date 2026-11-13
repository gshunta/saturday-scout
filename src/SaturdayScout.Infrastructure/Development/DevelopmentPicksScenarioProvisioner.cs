using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Identity;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure.Development;

public sealed record DevelopmentPicksScenarioResult(
    Guid LeagueId,
    Guid PlayerId,
    Guid SlateId,
    int Season,
    int Week,
    DateTimeOffset LockUtc,
    bool ReusedExistingSlate,
    bool UsesSyntheticGames);

/// <summary>
/// Creates a published picks scenario for local manual testing. The CLI that
/// invokes this type enforces Development plus the local saturday_scout target.
/// This operation does not create users, memberships, or pick sheets.
/// </summary>
public sealed class DevelopmentPicksScenarioProvisioner(
    SaturdayScoutDbContext db,
    ScoutIdentityDbContext identity)
{
    private const string SyntheticProvider = "saturday-scout-dev";
    private const int MinimumLockLeadHours = 48;

    public async Task<DevelopmentPicksScenarioResult> ProvisionAsync(
        Guid leagueId,
        Guid playerId,
        string scenarioKey,
        CancellationToken cancellationToken = default)
    {
        if (leagueId == Guid.Empty || playerId == Guid.Empty || !IsValidScenarioKey(scenarioKey))
            throw new InvalidOperationException("Supply valid league/player IDs and a scenario key containing only letters, digits, or hyphens.");

        if (!await db.Leagues.AsNoTracking().AnyAsync(x => x.Id == leagueId, cancellationToken))
            throw new InvalidOperationException("The requested development league does not exist.");
        if (!await db.LeagueMembers.AsNoTracking().AnyAsync(x => x.LeagueId == leagueId && x.UserId == playerId, cancellationToken))
            throw new InvalidOperationException("The requested player is not a member of the development league.");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == playerId, cancellationToken) ||
            !await identity.Users.AsNoTracking().AnyAsync(x => x.Id == playerId, cancellationToken) ||
            !await (from userRole in identity.UserRoles.AsNoTracking()
                    join role in identity.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                    where userRole.UserId == playerId && role.Name == ScoutRoles.Player
                    select userRole.UserId).AnyAsync(cancellationToken))
            throw new InvalidOperationException("The selected domain user must be linked to an Identity account with the Player role.");

        var idempotencyKey = $"{leagueId:D}:{scenarioKey}";
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AcquireProvisioningLockAsync(transaction.GetDbTransaction(), $"dev-picks-scenario:{leagueId:D}", cancellationToken);

        var slateId = DeterministicGuid($"slate:{idempotencyKey}");
        var existing = await db.WeeklySlates.AsNoTracking().Include(x => x.Games)
            .SingleOrDefaultAsync(x => x.Id == slateId, cancellationToken);
        if (existing is not null)
        {
            var now = await DatabaseNowAsync(cancellationToken);
            if (existing.LeagueId != leagueId || existing.Sport != Sport.CollegeFootball ||
                existing.Status != SlateStatus.Published || existing.LockUtc is null || existing.LockUtc <= now ||
                existing.Games.Count != 10 || existing.Games.Select(x => x.GameId).Distinct().Count() != 10)
                throw new InvalidOperationException(
                    "This scenario key already exists but is no longer an active published test slate. Use a new scenario key; existing data was not changed.");

            return Result(existing, playerId, reused: true, usesSynthetic: await db.Games.AsNoTracking()
                .Where(x => existing.Games.Select(g => g.GameId).Contains(x.Id))
                .AllAsync(x => x.Provider == SyntheticProvider, cancellationToken));
        }

        var nowUtc = await DatabaseNowAsync(cancellationToken);
        var lockUtc = nowUtc.AddHours(MinimumLockLeadHours);
        var (season, week, games, usesSynthetic) = await SelectGamesAsync(leagueId, scenarioKey,
            slateId, lockUtc, nowUtc, cancellationToken);

        var slate = new WeeklySlate(slateId, leagueId, Sport.CollegeFootball, season, week);
        slate.ReplaceGames(games);
        slate.Publish(games, lockUtc, nowUtc);
        db.WeeklySlates.Add(slate);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DevelopmentPicksScenarioResult(leagueId, playerId, slateId, season, week,
            lockUtc, ReusedExistingSlate: false, UsesSyntheticGames: usesSynthetic);
    }

    private async Task<(int Season, int Week, IReadOnlyList<Game> Games, bool Synthetic)> SelectGamesAsync(
        Guid leagueId,
        string scenarioKey,
        Guid slateId,
        DateTimeOffset lockUtc,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var season = nowUtc.Year;
        var occupiedWeeks = await db.WeeklySlates.AsNoTracking()
            .Where(x => x.LeagueId == leagueId && x.Sport == Sport.CollegeFootball && x.Season == season)
            .Select(x => x.Week).ToListAsync(cancellationToken);
        var highestUsedWeek = await db.Games.AsNoTracking()
            .Where(x => x.Sport == Sport.CollegeFootball && x.Season == season)
            .Select(x => (int?)x.Week).MaxAsync(cancellationToken) ?? 0;
        highestUsedWeek = Math.Max(highestUsedWeek, occupiedWeeks.DefaultIfEmpty().Max());

        var weekOrder = Enumerable.Range(1, 30)
            .OrderBy(week => week > highestUsedWeek ? week : week + 30)
            .ToArray();
        var eligibleGames = await db.Games.Where(x => x.Sport == Sport.CollegeFootball && x.Season == season &&
                x.KickoffUtc > lockUtc && x.Status != GameStatus.Final &&
                x.Status != GameStatus.Canceled && x.Status != GameStatus.NoContest)
            .OrderBy(x => x.KickoffUtc).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var week in weekOrder)
        {
            if (occupiedWeeks.Contains(week)) continue;
            var candidateGames = eligibleGames.Where(x => x.Week == week).Take(10).ToArray();
            if (candidateGames.Length == 10)
                return (season, week, candidateGames, false);
        }

        var syntheticWeek = weekOrder.FirstOrDefault(week => !occupiedWeeks.Contains(week));
        if (syntheticWeek == 0)
            throw new InvalidOperationException("No unoccupied college-football week is available for a development test slate.");

        var teams = await db.Teams.AsNoTracking().Where(x => x.Sport == Sport.CollegeFootball)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).Take(20).ToListAsync(cancellationToken);
        if (teams.Count < 20)
            throw new InvalidOperationException("At least 20 existing college teams are required to create synthetic test games.");

        var syntheticGames = Enumerable.Range(0, 10).Select(index => new Game(
            DeterministicGuid($"game:{slateId:D}:{index}"),
            Sport.CollegeFootball,
            season,
            syntheticWeek,
            teams[index * 2].Id,
            teams[index * 2 + 1].Id,
            lockUtc.AddHours(4 + index),
            SyntheticProvider,
            $"{scenarioKey}:{slateId:N}:{index:D2}"))
            .ToArray();

        db.Games.AddRange(syntheticGames);
        return (season, syntheticWeek, syntheticGames, true);
    }

    private async Task AcquireProvisioningLockAsync(
        System.Data.Common.DbTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken) => db.Database
        .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
        .SingleAsync(cancellationToken);

    private static DevelopmentPicksScenarioResult Result(
        WeeklySlate slate,
        Guid playerId,
        bool reused,
        bool usesSynthetic) => new(slate.LeagueId, playerId, slate.Id, slate.Season,
        slate.Week, slate.LockUtc!.Value, reused, usesSynthetic);

    private static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"SaturdayScout.DevPicksScenario.v1:{value}"))[..16]);

    private static bool IsValidScenarioKey(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 40 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
