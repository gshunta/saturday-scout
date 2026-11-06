using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Data.Common;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure;

public sealed class AdminScoutStore(SaturdayScoutDbContext db) : IAdminScoutService
{
    private static ScoutException Missing() => new(ScoutError.NotFound, "The requested resource was not found.");
    private static ScoutException Conflict(string message) => new(ScoutError.Conflict, message);

    public async Task<AdminDashboardDto> GetDashboardAsync(CancellationToken ct)
    {
        var college = db.Games.AsNoTracking().Where(x => x.Sport == Sport.CollegeFootball);
        var teams = await db.Teams.CountAsync(x => x.Sport == Sport.CollegeFootball, ct);
        var games = await college.CountAsync(ct);
        var leagues = await db.Leagues.CountAsync(ct);
        var slates = db.WeeklySlates.AsNoTracking().Where(x => x.Sport == Sport.CollegeFootball);
        var slateCount = await slates.CountAsync(ct);
        var draftCount = await slates.CountAsync(x => x.Status == SlateStatus.Draft, ct);
        var publishedCount = await slates.CountAsync(x => x.Status == SlateStatus.Published, ct);
        var import = await college.Where(x => x.SourceRetrievedUtc != null)
            .OrderByDescending(x => x.SourceRetrievedUtc).ThenByDescending(x => x.Id)
            .Select(x => new { x.SourceRetrievedUtc, x.Provider, x.SourceStatus }).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var upcoming = await (from game in db.Games.AsNoTracking()
            join home in db.Teams.AsNoTracking() on game.HomeTeamId equals home.Id
            join away in db.Teams.AsNoTracking() on game.AwayTeamId equals away.Id
            where game.Sport == Sport.CollegeFootball && game.Status == GameStatus.Scheduled && game.KickoffUtc >= now
            orderby game.KickoffUtc, game.Id
            select new AdminGameDto(game.Id, ScoutSport.Format(game.Sport), game.Season, game.Week,
                game.HomeTeamId, game.AwayTeamId, home.Name, away.Name, game.KickoffUtc,
                game.Status.ToString(), game.HomeScore, game.AwayScore, null, null))
            .Take(10).ToListAsync(ct);
        return new AdminDashboardDto(teams, games, leagues, slateCount, draftCount, publishedCount,
            import?.SourceRetrievedUtc, import?.Provider, import?.SourceStatus, upcoming);
    }

    public async Task<AdminGamesPageDto> BrowseGamesAsync(AdminGameQuery query, CancellationToken ct)
    {
        AdminGameQueryValidation.Validate(query);
        var rows = from game in db.Games.AsNoTracking()
            join home in db.Teams.AsNoTracking() on game.HomeTeamId equals home.Id
            join away in db.Teams.AsNoTracking() on game.AwayTeamId equals away.Id
            where game.Sport == Sport.CollegeFootball
            select new { Game = game, Home = home, Away = away };
        if (query.Season is not null) rows = rows.Where(x => x.Game.Season == query.Season);
        if (query.Week is not null) rows = rows.Where(x => x.Game.Week == query.Week);
        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<GameStatus>(query.Status, true, out var status))
            rows = rows.Where(x => x.Game.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = $"%{query.Search.Trim()}%";
            rows = rows.Where(x => EF.Functions.ILike(x.Home.Name, term) ||
                EF.Functions.ILike(x.Away.Name, term) || EF.Functions.ILike(x.Home.Abbreviation, term) ||
                EF.Functions.ILike(x.Away.Abbreviation, term));
        }

        var total = await rows.CountAsync(ct);
        rows = query.KickoffDescending
            ? rows.OrderByDescending(x => x.Game.KickoffUtc).ThenBy(x => x.Game.Id)
            : rows.OrderBy(x => x.Game.KickoffUtc).ThenBy(x => x.Game.Id);
        var items = await rows.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new AdminGameDto(x.Game.Id, ScoutSport.Format(x.Game.Sport), x.Game.Season,
                x.Game.Week, x.Game.HomeTeamId, x.Game.AwayTeamId, x.Home.Name, x.Away.Name,
                x.Game.KickoffUtc, x.Game.Status.ToString(), x.Game.HomeScore, x.Game.AwayScore, null, null))
            .ToListAsync(ct);
        var seasons = await db.Games.AsNoTracking().Where(x => x.Sport == Sport.CollegeFootball)
            .Select(x => x.Season).Distinct().OrderByDescending(x => x).ToListAsync(ct);
        var weeks = await db.Games.AsNoTracking().Where(x => x.Sport == Sport.CollegeFootball &&
                (query.Season == null || x.Season == query.Season))
            .Select(x => x.Week).Distinct().OrderBy(x => x).ToListAsync(ct);
        return new AdminGamesPageDto(items, total, query.Page, query.PageSize, seasons, weeks,
            ConferenceFilterAvailable: false);
    }

    public async Task<IReadOnlyList<AdminLeagueDto>> GetLeaguesAsync(CancellationToken ct) =>
        await db.Leagues.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new AdminLeagueDto(x.Id, x.Name)).ToListAsync(ct);

    public async Task<IReadOnlyList<AdminCommissionerDto>> GetEligibleCommissionersAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(x => x.DisplayName).ThenBy(x => x.Email)
            .Select(x => new AdminCommissionerDto(x.Id, x.DisplayName, x.Email)).ToListAsync(ct);

    public async Task<AdminLeagueDto> CreateLeagueAsync(CreateAdminLeagueRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 ||
            request.CommissionerUserId == Guid.Empty)
            throw new ScoutException(ScoutError.Invalid, "Enter a league name of 1 to 200 characters and select a commissioner.");

        var name = request.Name.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var commissionerExists = await db.Users.AsNoTracking()
            .AnyAsync(x => x.Id == request.CommissionerUserId, ct);
        if (!commissionerExists)
            throw new ScoutException(ScoutError.Invalid, "The selected commissioner is not an existing user.");

        // Repeated submissions for the same league and commissioner are rejected. Serializable
        // isolation also makes simultaneous duplicate requests conflict without requiring a schema change.
        var duplicate = await db.Leagues.AsNoTracking().AnyAsync(x =>
            x.CommissionerUserId == request.CommissionerUserId && x.Name == name, ct);
        if (duplicate) throw Conflict("A league with this name already exists for the selected commissioner.");

        var now = DateTimeOffset.UtcNow;
        var (league, membership) = LeagueCreation.Create(name, request.CommissionerUserId, now);
        db.Leagues.Add(league);
        db.LeagueMembers.Add(membership);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception exception) when (exception is PostgresException
            { SqlState: PostgresErrorCodes.SerializationFailure } ||
            exception is DbUpdateException
            { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } })
        {
            throw Conflict("The league could not be created because another request changed the same league data. Please retry.");
        }
        return new AdminLeagueDto(league.Id, league.Name);
    }

    public async Task<IReadOnlyList<AdminSlateSummaryDto>> GetSlatesAsync(CancellationToken ct)
    {
        var slates = await (from slate in db.WeeklySlates.AsNoTracking()
            join league in db.Leagues.AsNoTracking() on slate.LeagueId equals league.Id
            where slate.Sport == Sport.CollegeFootball
            orderby slate.Season descending, slate.Week descending, league.Name
            select new { slate.Id, slate.LeagueId, LeagueName = league.Name, slate.Season, slate.Week,
                slate.Status, GameCount = slate.Games.Count }).ToListAsync(ct);
        var result = new List<AdminSlateSummaryDto>(slates.Count);
        foreach (var slate in slates)
        {
            var version = await ReadVersionAsync(slate.Id, null, ct);
            result.Add(new AdminSlateSummaryDto(slate.Id, slate.LeagueId, slate.LeagueName,
                slate.Season, slate.Week, slate.Status.ToString(), slate.GameCount,
                slate.Status == SlateStatus.Draft && slate.GameCount == 10, version));
        }
        return result;
    }

    public async Task<AdminSlateDto> GetSlateAsync(Guid slateId, CancellationToken ct)
    {
        var slate = await db.WeeklySlates.AsNoTracking().Include(x => x.Games)
            .SingleOrDefaultAsync(x => x.Id == slateId && x.Sport == Sport.CollegeFootball, ct) ?? throw Missing();
        var leagueName = await db.Leagues.AsNoTracking().Where(x => x.Id == slate.LeagueId)
            .Select(x => x.Name).SingleAsync(ct);
        var games = await SlateGameRows(slate.Games.OrderBy(x => x.DisplayOrder).Select(x => x.GameId), ct);
        var version = await ReadVersionAsync(slateId, null, ct);
        return new AdminSlateDto(slate.Id, slate.LeagueId, leagueName, slate.Season,
            slate.Week, slate.Status.ToString(), slate.LockUtc, slate.PublishedUtc,
            slate.Status == SlateStatus.Draft && games.Count == 10, version, games);
    }

    public async Task<AdminSlateDto> CreateSlateAsync(CreateAdminSlateRequest request, CancellationToken ct)
    {
        if (request.LeagueId == Guid.Empty || request.Season is < 1900 or > 9999 || request.Week is < 1 or > 30)
            throw new ScoutException(ScoutError.Invalid, "Choose a league and valid season and week.");
        if (!await db.Leagues.AnyAsync(x => x.Id == request.LeagueId, ct)) throw Missing();
        var slate = new WeeklySlate(Guid.NewGuid(), request.LeagueId, Sport.CollegeFootball,
            request.Season, request.Week);
        db.WeeklySlates.Add(slate);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(slate).State = EntityState.Detached;
            throw Conflict("A college football slate already exists for this league, season, and week.");
        }
        return await GetSlateAsync(slate.Id, ct);
    }

    public async Task<AdminSlateDto> UpdateSlateGamesAsync(Guid slateId,
        UpdateAdminSlateGamesRequest request, CancellationToken ct)
    {
        if (request.GameIds is null || request.GameIds.Count > 10 ||
            request.GameIds.Any(x => x == Guid.Empty) || request.GameIds.Distinct().Count() != request.GameIds.Count ||
            request.ExpectedVersion < 0)
            throw new ScoutException(ScoutError.Invalid, "Choose up to 10 distinct games and provide the current draft version.");

        // Blazor scopes can persist across many event callbacks. Start from a fresh
        // aggregate snapshot so prior edits do not leave stale SlateGame entities tracked.
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var currentVersion = await ReadVersionAsync(slateId, transaction.GetDbTransaction(), ct, forUpdate: true);
        if (currentVersion < 0) throw Missing();
        if (currentVersion != request.ExpectedVersion) throw Conflict("This draft changed since it was loaded. Reload it before saving.");

        var slate = await db.WeeklySlates.SingleOrDefaultAsync(x => x.Id == slateId &&
            x.Sport == Sport.CollegeFootball, ct) ?? throw Missing();
        if (slate.Status != SlateStatus.Draft) throw Conflict("Only draft slates can be edited.");
        if (await db.PickSheets.AnyAsync(x => x.SlateId == slateId && x.Status == PickSheetStatus.Submitted, ct))
            throw Conflict("A slate with submitted pick sheets cannot be edited.");

        var games = await db.Games.Where(x => request.GameIds.Contains(x.Id)).ToListAsync(ct);
        if (games.Count != request.GameIds.Count)
            throw new ScoutException(ScoutError.Invalid, "One or more selected games do not exist.");
        var gameMap = games.ToDictionary(x => x.Id);
        var ordered = request.GameIds.Select(id => gameMap[id]).ToArray();
        try { slate.ReplaceGames(ordered); }
        catch (ArgumentException exception)
        { throw new ScoutException(ScoutError.Invalid, exception.Message); }

        await db.SlateGames.Where(x => x.SlateId == slateId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        var bumpedVersion = await BumpVersionAsync(slateId, transaction.GetDbTransaction(), ct);
        await transaction.CommitAsync(ct);
        var updated = await GetSlateAsync(slateId, ct);
        return updated with { Version = bumpedVersion };
    }

    public async Task<AdminSlateDto> RandomizeSlateGamesAsync(Guid slateId,
        RandomizeAdminSlateRequest request, CancellationToken ct)
    {
        if (request.ExpectedVersion < 0)
            throw new ScoutException(ScoutError.Invalid, "A current draft version is required.");
        var slate = await db.WeeklySlates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == slateId && x.Sport == Sport.CollegeFootball, ct) ?? throw Missing();
        if (slate.Status != SlateStatus.Draft) throw Conflict("Only draft slates can be randomized.");
        var games = await db.Games.AsNoTracking().Where(x => x.Sport == slate.Sport &&
            x.Season == slate.Season && x.Week == slate.Week).ToListAsync(ct);
        var selection = new WeeklySlate(slate.Id, slate.LeagueId, slate.Sport, slate.Season, slate.Week);
        try { selection.RandomizeGames(games); }
        catch (InvalidOperationException exception)
        { throw new ScoutException(ScoutError.Invalid, exception.Message); }
        return await UpdateSlateGamesAsync(slateId,
            new UpdateAdminSlateGamesRequest(selection.Games.Select(x => x.GameId).ToArray(), request.ExpectedVersion), ct);
    }

    public async Task<AdminSlateDto> PublishSlateAsync(Guid slateId,
        PublishAdminSlateRequest request, CancellationToken ct)
    {
        if (request.ExpectedVersion < 0 || request.LockUtc.Offset != TimeSpan.Zero)
            throw new ScoutException(ScoutError.Invalid, "A current draft version and UTC lock time are required.");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var currentVersion = await ReadVersionAsync(slateId, transaction.GetDbTransaction(), ct, forUpdate: true);
        if (currentVersion < 0) throw Missing();
        if (currentVersion != request.ExpectedVersion)
            throw Conflict("This draft changed since it was loaded. Reload it before publishing.");

        var slate = await db.WeeklySlates.Include(x => x.Games)
            .SingleOrDefaultAsync(x => x.Id == slateId && x.Sport == Sport.CollegeFootball, ct) ?? throw Missing();
        if (slate.Status != SlateStatus.Draft) throw Conflict("Only draft slates can be published.");
        if (await db.PickSheets.AnyAsync(x => x.SlateId == slateId && x.Status == PickSheetStatus.Submitted, ct))
            throw Conflict("A slate with submitted pick sheets cannot be published again.");
        var gameIds = slate.Games.Select(x => x.GameId).ToArray();
        var games = await db.Games.Where(x => gameIds.Contains(x.Id)).ToListAsync(ct);
        slate.Publish(games, request.LockUtc, await DatabaseNowAsync(ct));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetSlateAsync(slateId, ct);
    }

    private async Task<List<AdminGameDto>> SlateGameRows(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var orderedIds = ids.ToArray();
        if (orderedIds.Length == 0) return [];
        var rows = await (from game in db.Games.AsNoTracking()
            join home in db.Teams.AsNoTracking() on game.HomeTeamId equals home.Id
            join away in db.Teams.AsNoTracking() on game.AwayTeamId equals away.Id
            where orderedIds.Contains(game.Id)
            select new AdminGameDto(game.Id, ScoutSport.Format(game.Sport), game.Season, game.Week,
                game.HomeTeamId, game.AwayTeamId, home.Name, away.Name, game.KickoffUtc,
                game.Status.ToString(), game.HomeScore, game.AwayScore, null, null)).ToListAsync(ct);
        var map = rows.ToDictionary(x => x.Id);
        return orderedIds.Where(map.ContainsKey).Select(id => map[id]).ToList();
    }

    private async Task<long> ReadVersionAsync(Guid id, DbTransaction? transaction,
        CancellationToken ct, bool forUpdate = false)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT xmin::text::bigint FROM \"WeeklySlates\" WHERE \"Id\" = @id{(forUpdate ? " FOR UPDATE" : "")}";
            command.Transaction = transaction;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = id;
            command.Parameters.Add(parameter);
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? -1 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }

    private async Task<long> BumpVersionAsync(Guid id, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "UPDATE \"WeeklySlates\" SET \"Week\" = \"Week\" WHERE \"Id\" = @id RETURNING xmin::text::bigint";
        command.Transaction = transaction;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private Task<DateTimeOffset> DatabaseNowAsync(CancellationToken ct) => db.Database
        .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

}
