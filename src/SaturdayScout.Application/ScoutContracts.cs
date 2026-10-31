using SaturdayScout.Domain;

namespace SaturdayScout.Application;

public enum ScoutError { Invalid, Forbidden, NotFound, Conflict }

public sealed class ScoutException(ScoutError error, string message) : Exception(message)
{
    public ScoutError Error { get; } = error;
}

public sealed record GameDto(Guid Id, string Sport, int Season, int Week, Guid HomeTeamId,
    Guid AwayTeamId, DateTimeOffset KickoffUtc, string Status, int? HomeScore, int? AwayScore);
public sealed record LeagueDto(Guid Id, string Name, Guid CommissionerUserId);
public sealed record MemberDto(Guid UserId, string DisplayName, string Role, DateTimeOffset JoinedUtc);
public sealed record SlateGameDto(int DisplayOrder, GameDto Game);
public sealed record SlateDto(Guid Id, Guid LeagueId, string Sport, int Season, int Week,
    string Status, DateTimeOffset? LockUtc, DateTimeOffset? PublishedUtc, IReadOnlyList<SlateGameDto> Games);
public sealed record PickDto(Guid GameId, Guid SelectedTeamId, int ConfidenceRank);
public sealed record PickSheetDto(Guid Id, Guid SlateId, string Status, DateTimeOffset? SubmittedUtc,
    long Version, IReadOnlyList<PickDto> Picks);
public sealed record StandingDto(Guid UserId, string DisplayName, int Points, int CorrectPicks, int PendingGames);
public sealed record LeaderboardDto(Guid LeagueId, string Scope, int Season, int? Week, IReadOnlyList<StandingDto> Entries);

public sealed record CreateLeagueRequest(string Name);
public sealed record AddMemberRequest(string ExternalSubjectId);
public sealed record CreateSlateRequest(string Sport, int Season, int Week);
public sealed record SetSlateGamesRequest(IReadOnlyList<Guid> GameIds);
public sealed record PublishSlateRequest(DateTimeOffset LockUtc);
public sealed record SavePicksRequest(IReadOnlyList<PickDto> Picks, long? ExpectedVersion);

public sealed record ImportedResult(Guid GameId, GameStatus Status, int? HomeScore, int? AwayScore);

public interface IResultImportService
{
    Task UpdateResultAsync(ImportedResult result, CancellationToken cancellationToken);
}

public interface IScoutService
{
    Task<IReadOnlyList<GameDto>> BrowseGamesAsync(Guid actorId, string sport, int season, int week, int page, int pageSize, CancellationToken ct);
    Task<LeagueDto> CreateLeagueAsync(Guid actorId, CreateLeagueRequest request, CancellationToken ct);
    Task<LeagueDto> GetLeagueAsync(Guid actorId, Guid leagueId, CancellationToken ct);
    Task<MemberDto> AddMemberAsync(Guid actorId, Guid leagueId, AddMemberRequest request, CancellationToken ct);
    Task<SlateDto> CreateSlateAsync(Guid actorId, Guid leagueId, CreateSlateRequest request, CancellationToken ct);
    Task<SlateDto> SetSlateGamesAsync(Guid actorId, Guid slateId, SetSlateGamesRequest request, CancellationToken ct);
    Task<SlateDto> PublishSlateAsync(Guid actorId, Guid slateId, PublishSlateRequest request, CancellationToken ct);
    Task<SlateDto> GetSlateAsync(Guid actorId, Guid slateId, CancellationToken ct);
    Task<PickSheetDto?> GetMySheetAsync(Guid actorId, Guid slateId, CancellationToken ct);
    Task<PickSheetDto> SavePicksAsync(Guid actorId, Guid slateId, SavePicksRequest request, CancellationToken ct);
    Task<PickSheetDto> SubmitAsync(Guid actorId, Guid slateId, long? expectedVersion, CancellationToken ct);
    Task<LeaderboardDto> GetLeaderboardAsync(Guid actorId, Guid leagueId, string scope, int season, int? week, CancellationToken ct);
}

public interface IScoutStore : IScoutService, IResultImportService;

public static class ScoutSport
{
    public static Sport Parse(string value) => value?.ToLowerInvariant() switch
    {
        "college" => Sport.CollegeFootball,
        "nfl" => Sport.Nfl,
        _ => throw new ScoutException(ScoutError.Invalid, "Sport must be 'college' or 'nfl'.")
    };

    public static string Format(Sport sport) => sport == Sport.CollegeFootball ? "college" : "nfl";
}

public sealed class ScoutService(IScoutStore store) : IScoutService
{
    public Task<IReadOnlyList<GameDto>> BrowseGamesAsync(Guid actorId, string sport, int season, int week, int page, int pageSize, CancellationToken ct)
    {
        ScoutSport.Parse(sport);
        if (season is < 1900 or > 9999 || week is < 1 or > 30 || page < 1 || pageSize is < 1 or > 100)
            throw new ScoutException(ScoutError.Invalid, "Invalid season, week, or pagination.");
        return store.BrowseGamesAsync(actorId, sport, season, week, page, pageSize, ct);
    }

    public Task<LeagueDto> CreateLeagueAsync(Guid actorId, CreateLeagueRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            throw new ScoutException(ScoutError.Invalid, "League name must be 1 to 200 characters.");
        return store.CreateLeagueAsync(actorId, request, ct);
    }

    public Task<LeagueDto> GetLeagueAsync(Guid actorId, Guid leagueId, CancellationToken ct) => store.GetLeagueAsync(actorId, leagueId, ct);
    public Task<MemberDto> AddMemberAsync(Guid actorId, Guid leagueId, AddMemberRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalSubjectId) || request.ExternalSubjectId.Length > 255)
            throw new ScoutException(ScoutError.Invalid, "External subject ID is required.");
        return store.AddMemberAsync(actorId, leagueId, request, ct);
    }
    public Task<SlateDto> CreateSlateAsync(Guid actorId, Guid leagueId, CreateSlateRequest request, CancellationToken ct)
    {
        ScoutSport.Parse(request.Sport);
        if (request.Season is < 1900 or > 9999 || request.Week is < 1 or > 30)
            throw new ScoutException(ScoutError.Invalid, "Invalid season or week.");
        return store.CreateSlateAsync(actorId, leagueId, request, ct);
    }
    public Task<SlateDto> SetSlateGamesAsync(Guid actorId, Guid slateId, SetSlateGamesRequest request, CancellationToken ct)
    {
        if (request.GameIds is null || request.GameIds.Count != 10 || request.GameIds.Any(id => id == Guid.Empty) || request.GameIds.Distinct().Count() != 10)
            throw new ScoutException(ScoutError.Invalid, "Exactly 10 distinct game IDs are required.");
        return store.SetSlateGamesAsync(actorId, slateId, request, ct);
    }
    public Task<SlateDto> PublishSlateAsync(Guid actorId, Guid slateId, PublishSlateRequest request, CancellationToken ct)
    {
        if (request.LockUtc.Offset != TimeSpan.Zero)
            throw new ScoutException(ScoutError.Invalid, "Lock time must be UTC.");
        return store.PublishSlateAsync(actorId, slateId, request, ct);
    }
    public Task<SlateDto> GetSlateAsync(Guid actorId, Guid slateId, CancellationToken ct) => store.GetSlateAsync(actorId, slateId, ct);
    public Task<PickSheetDto?> GetMySheetAsync(Guid actorId, Guid slateId, CancellationToken ct) => store.GetMySheetAsync(actorId, slateId, ct);
    public Task<PickSheetDto> SavePicksAsync(Guid actorId, Guid slateId, SavePicksRequest request, CancellationToken ct)
    {
        if (request.Picks is null || request.Picks.Count > 10 || request.ExpectedVersion < 0)
            throw new ScoutException(ScoutError.Invalid, "Invalid picks or version.");
        return store.SavePicksAsync(actorId, slateId, request, ct);
    }
    public Task<PickSheetDto> SubmitAsync(Guid actorId, Guid slateId, long? expectedVersion, CancellationToken ct)
    {
        if (expectedVersion < 0) throw new ScoutException(ScoutError.Invalid, "Invalid version.");
        return store.SubmitAsync(actorId, slateId, expectedVersion, ct);
    }
    public Task<LeaderboardDto> GetLeaderboardAsync(Guid actorId, Guid leagueId, string scope, int season, int? week, CancellationToken ct)
    {
        if (scope is not ("college" or "nfl" or "combined") || season is < 1900 or > 9999 || week is < 1 or > 30)
            throw new ScoutException(ScoutError.Invalid, "Invalid scope, season, or week.");
        return store.GetLeaderboardAsync(actorId, leagueId, scope, season, week, ct);
    }
}
