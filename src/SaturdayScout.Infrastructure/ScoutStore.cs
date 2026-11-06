using Microsoft.EntityFrameworkCore;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure;

public sealed class ScoutStore(SaturdayScoutDbContext db) : IScoutStore
{
    private static ScoutException Missing() => new(ScoutError.NotFound, "Resource was not found.");
    private static ScoutException Forbidden() => new(ScoutError.Forbidden, "League membership or commissioner role is required.");
    private static ScoutException Conflict(string message) => new(ScoutError.Conflict, message);

    private async Task RequireMember(Guid userId, Guid leagueId, CancellationToken ct)
    {
        if (!await db.LeagueMembers.AnyAsync(x => x.LeagueId == leagueId && x.UserId == userId, ct))
            throw Forbidden();
    }

    private async Task RequireCommissioner(Guid userId, Guid leagueId, CancellationToken ct)
    {
        if (!await db.LeagueMembers.AnyAsync(x => x.LeagueId == leagueId && x.UserId == userId && x.Role == MembershipRole.Commissioner, ct))
            throw Forbidden();
    }

    private async Task<WeeklySlate> FindSlate(Guid slateId, CancellationToken ct) =>
        await db.WeeklySlates.Include(x => x.Games).SingleOrDefaultAsync(x => x.Id == slateId, ct) ?? throw Missing();

    private static GameDto Map(Game game) => new(game.Id, ScoutSport.Format(game.Sport), game.Season, game.Week,
        game.HomeTeamId, game.AwayTeamId, game.KickoffUtc, game.Status.ToString(), game.HomeScore, game.AwayScore);

    private async Task<SlateDto> Map(WeeklySlate slate, CancellationToken ct)
    {
        var ids = slate.Games.Select(x => x.GameId).ToArray();
        var games = await db.Games.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return new SlateDto(slate.Id, slate.LeagueId, ScoutSport.Format(slate.Sport), slate.Season, slate.Week,
            slate.Status.ToString(), slate.LockUtc, slate.PublishedUtc,
            slate.Games.OrderBy(x => x.DisplayOrder).Select(x => new SlateGameDto(x.DisplayOrder, Map(games[x.GameId]))).ToArray());
    }

    private static PickSheetDto Map(PickSheet sheet) => new(sheet.Id, sheet.SlateId, sheet.Status.ToString(),
        sheet.SubmittedUtc, sheet.Version, sheet.Picks.OrderBy(x => x.ConfidenceRank)
            .Select(x => new PickDto(x.GameId, x.SelectedTeamId, x.ConfidenceRank)).ToArray());

    public async Task<IReadOnlyList<GameDto>> BrowseGamesAsync(Guid actorId, string sport, int season, int week,
        int page, int pageSize, CancellationToken ct)
    {
        if (!await db.LeagueMembers.AnyAsync(x => x.UserId == actorId, ct)) throw Forbidden();
        var parsed = ScoutSport.Parse(sport);
        return await db.Games.AsNoTracking().Where(x => x.Sport == parsed && x.Season == season && x.Week == week)
            .OrderBy(x => x.KickoffUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new GameDto(x.Id, sport, x.Season, x.Week, x.HomeTeamId, x.AwayTeamId,
                x.KickoffUtc, x.Status.ToString(), x.HomeScore, x.AwayScore)).ToListAsync(ct);
    }

    public async Task<LeagueDto> CreateLeagueAsync(Guid actorId, CreateLeagueRequest request, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(x => x.Id == actorId, ct)) throw Forbidden();
        var now = DateTimeOffset.UtcNow;
        var (league, membership) = LeagueCreation.Create(request.Name, actorId, now);
        db.Leagues.Add(league);
        db.LeagueMembers.Add(membership);
        await db.SaveChangesAsync(ct);
        return new LeagueDto(league.Id, league.Name, actorId);
    }

    public async Task<LeagueDto> GetLeagueAsync(Guid actorId, Guid leagueId, CancellationToken ct)
    {
        var league = await db.Leagues.AsNoTracking().SingleOrDefaultAsync(x => x.Id == leagueId, ct) ?? throw Missing();
        await RequireMember(actorId, leagueId, ct);
        return new LeagueDto(league.Id, league.Name, league.CommissionerUserId);
    }

    public async Task<MemberDto> AddMemberAsync(Guid actorId, Guid leagueId, AddMemberRequest request, CancellationToken ct)
    {
        if (!await db.Leagues.AnyAsync(x => x.Id == leagueId, ct)) throw Missing();
        await RequireCommissioner(actorId, leagueId, ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.ExternalSubjectId == request.ExternalSubjectId, ct) ?? throw Missing();
        if (await db.LeagueMembers.AnyAsync(x => x.LeagueId == leagueId && x.UserId == user.Id, ct))
            throw Conflict("User is already a league member.");
        var member = new LeagueMember(leagueId, user.Id, MembershipRole.Member, DateTimeOffset.UtcNow);
        db.LeagueMembers.Add(member);
        await db.SaveChangesAsync(ct);
        return new MemberDto(user.Id, user.DisplayName, member.Role.ToString(), member.JoinedUtc);
    }

    public async Task<SlateDto> CreateSlateAsync(Guid actorId, Guid leagueId, CreateSlateRequest request, CancellationToken ct)
    {
        if (!await db.Leagues.AnyAsync(x => x.Id == leagueId, ct)) throw Missing();
        await RequireCommissioner(actorId, leagueId, ct);
        var slate = new WeeklySlate(Guid.NewGuid(), leagueId, ScoutSport.Parse(request.Sport), request.Season, request.Week);
        db.WeeklySlates.Add(slate);
        await db.SaveChangesAsync(ct);
        return await Map(slate, ct);
    }

    public async Task<SlateDto> SetSlateGamesAsync(Guid actorId, Guid slateId, SetSlateGamesRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSlateRow(slateId, ct);
        var slate = await db.WeeklySlates.SingleOrDefaultAsync(x => x.Id == slateId, ct) ?? throw Missing();
        await RequireCommissioner(actorId, slate.LeagueId, ct);
        var games = await db.Games.Where(x => request.GameIds.Contains(x.Id)).ToListAsync(ct);
        if (games.Count != 10) throw new ScoutException(ScoutError.Invalid, "Every selected game must exist.");
        var ordered = request.GameIds.Select(id => games.Single(g => g.Id == id)).ToArray();
        slate.ReplaceGames(ordered);
        await db.SlateGames.Where(x => x.SlateId == slateId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Map(slate, ct);
    }

    public async Task<SlateDto> PublishSlateAsync(Guid actorId, Guid slateId, PublishSlateRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSlateRow(slateId, ct);
        var slate = await FindSlate(slateId, ct);
        await RequireCommissioner(actorId, slate.LeagueId, ct);
        var ids = slate.Games.Select(x => x.GameId).ToArray();
        var games = await db.Games.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        slate.Publish(games, request.LockUtc, await DbNow(ct));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Map(slate, ct);
    }

    public async Task<SlateDto> GetSlateAsync(Guid actorId, Guid slateId, CancellationToken ct)
    {
        var slate = await FindSlate(slateId, ct);
        await RequireMember(actorId, slate.LeagueId, ct);
        return await Map(slate, ct);
    }

    public async Task<PickSheetDto?> GetMySheetAsync(Guid actorId, Guid slateId, CancellationToken ct)
    {
        var slate = await db.WeeklySlates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == slateId, ct) ?? throw Missing();
        await RequireMember(actorId, slate.LeagueId, ct);
        var sheet = await db.PickSheets.Include(x => x.Picks).SingleOrDefaultAsync(x => x.SlateId == slateId && x.UserId == actorId, ct);
        return sheet is null ? null : Map(sheet);
    }

    public async Task<PickSheetDto> SavePicksAsync(Guid actorId, Guid slateId, SavePicksRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSlateRow(slateId, ct);
        var slate = await FindSlate(slateId, ct);
        await RequireMember(actorId, slate.LeagueId, ct);
        var now = await DbNow(ct);
        if (slate.Status != SlateStatus.Published || slate.LockUtc <= now)
            throw Conflict("Slate is not open for picks.");
        var sheet = await db.PickSheets.SingleOrDefaultAsync(x => x.SlateId == slateId && x.UserId == actorId, ct);
        if (sheet is null)
        {
            if (request.ExpectedVersion is not null and not 0) throw Conflict("Sheet version does not match.");
            sheet = new PickSheet(Guid.NewGuid(), slateId, actorId);
            db.PickSheets.Add(sheet);
        }
        else if (request.ExpectedVersion != sheet.Version)
            throw Conflict("Sheet version does not match.");
        if (sheet.Status == PickSheetStatus.Submitted) throw Conflict("Submitted sheets are immutable.");
        var ids = slate.Games.Select(x => x.GameId).ToArray();
        var games = await db.Games.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        sheet.ReplaceDraftPicks(slate, games, request.Picks.Select(p => (p.GameId, p.SelectedTeamId, p.ConfidenceRank)), now);
        await db.Picks.Where(x => x.SheetId == sheet.Id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await EnsureBeforeLock(slateId, ct);
        await tx.CommitAsync(ct);
        return Map(sheet);
    }

    public async Task<PickSheetDto> SubmitAsync(Guid actorId, Guid slateId, long? expectedVersion, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSlateRow(slateId, ct);
        var slate = await FindSlate(slateId, ct);
        await RequireMember(actorId, slate.LeagueId, ct);
        var now = await DbNow(ct);
        if (slate.Status != SlateStatus.Published || slate.LockUtc <= now)
            throw Conflict("Slate is not open for picks.");
        var sheet = await db.PickSheets.Include(x => x.Picks)
            .SingleOrDefaultAsync(x => x.SlateId == slateId && x.UserId == actorId, ct) ?? throw Conflict("Save a complete sheet first.");
        if (sheet.Status == PickSheetStatus.Submitted) throw Conflict("Submitted sheets are immutable.");
        if (expectedVersion != sheet.Version) throw Conflict("Sheet version does not match.");
        sheet.Submit(slate, now);
        await db.SaveChangesAsync(ct);
        await EnsureBeforeLock(slateId, ct);
        await tx.CommitAsync(ct);
        return Map(sheet);
    }

    public async Task<LeaderboardDto> GetLeaderboardAsync(Guid actorId, Guid leagueId, string scope, int season, int? week, CancellationToken ct)
    {
        if (!await db.Leagues.AnyAsync(x => x.Id == leagueId, ct)) throw Missing();
        await RequireMember(actorId, leagueId, ct);
        var members = await db.LeagueMembers.AsNoTracking().Where(x => x.LeagueId == leagueId).ToListAsync(ct);
        var memberIds = members.Select(x => x.UserId).ToArray();
        var users = await db.Users.AsNoTracking().Where(x => memberIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var slates = await db.WeeklySlates.AsNoTracking().Where(x => x.LeagueId == leagueId && x.Season == season &&
            (week == null || x.Week == week) && (scope == "combined" || x.Sport == ScoutSport.Parse(scope)))
            .ToListAsync(ct);
        var slateIds = slates.Select(x => x.Id).ToArray();
        var sheets = await db.PickSheets.AsNoTracking().Include(x => x.Picks)
            .Where(x => slateIds.Contains(x.SlateId) && x.Status == PickSheetStatus.Submitted).ToListAsync(ct);
        var gameIds = sheets.SelectMany(x => x.Picks).Select(x => x.GameId).Distinct().ToArray();
        var games = await db.Games.AsNoTracking().Where(x => gameIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var scores = sheets.SelectMany(x => x.Picks.Select(p => (x.UserId, Score: ConfidenceScoringService.Score(p, games[p.GameId]))))
            .GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => new
            {
                Points = g.Sum(x => x.Score.AwardedPoints),
                Correct = g.Count(x => x.Score.Outcome == PickOutcome.Correct),
                Pending = g.Count(x => x.Score.Outcome == PickOutcome.Pending)
            });
        var entries = members.Select(x =>
        {
            scores.TryGetValue(x.UserId, out var score);
            return new StandingDto(x.UserId, users[x.UserId].DisplayName, score?.Points ?? 0,
                score?.Correct ?? 0, score?.Pending ?? 0);
        }).OrderByDescending(x => x.Points).ThenBy(x => x.DisplayName, StringComparer.Ordinal).ThenBy(x => x.UserId).ToArray();
        return new LeaderboardDto(leagueId, scope, season, week, entries);
    }

    public async Task UpdateResultAsync(ImportedResult result, CancellationToken ct)
    {
        var game = await db.Games.SingleOrDefaultAsync(x => x.Id == result.GameId, ct) ?? throw Missing();
        if (result.Status == GameStatus.Final)
        {
            if (result.HomeScore is null || result.AwayScore is null)
                throw new ScoutException(ScoutError.Invalid, "Final results require both scores.");
            game.SetFinalScore(result.HomeScore.Value, result.AwayScore.Value);
        }
        else
        {
            if (result.HomeScore is not null || result.AwayScore is not null)
                throw new ScoutException(ScoutError.Invalid, "Only final results may include scores.");
            game.SetStatus(result.Status);
        }
        await db.SaveChangesAsync(ct);
    }

    private Task LockSlateRow(Guid slateId, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT 1 FROM \"WeeklySlates\" WHERE \"Id\" = {slateId} FOR UPDATE", ct);

    private Task<DateTimeOffset> DbNow(CancellationToken ct) => db.Database
        .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private async Task EnsureBeforeLock(Guid slateId, CancellationToken ct)
    {
        var beforeLock = await db.Database.SqlQuery<bool>(
            $"SELECT \"LockUtc\" > clock_timestamp() AS \"Value\" FROM \"WeeklySlates\" WHERE \"Id\" = {slateId}").SingleAsync(ct);
        if (!beforeLock) throw Conflict("Slate lock deadline has passed.");
    }
}
