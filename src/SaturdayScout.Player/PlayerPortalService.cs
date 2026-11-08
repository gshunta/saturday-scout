using System.Net.Http.Json;
using Microsoft.JSInterop;
using SaturdayScout.Application;

namespace SaturdayScout.Player;

public sealed class PlayerPortalService(IPlayerApiClient api)
{
    public Task<PlayerApiResult<IReadOnlyList<LeagueDto>>> GetMyLeaguesAsync(CancellationToken ct = default) =>
        api.SendAsync<IReadOnlyList<LeagueDto>>(new HttpRequestMessage(HttpMethod.Get, "/api/v1/leagues/me"), ct);

    public Task<PlayerApiResult<PlayerSlateDto>> GetActiveCollegeSlateAsync(Guid leagueId, CancellationToken ct = default) =>
        api.SendAsync<PlayerSlateDto>(new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/leagues/{leagueId:D}/slates/active"), ct);

    public Task<PlayerApiResult<PickSheetDto>> GetMySheetAsync(Guid slateId, CancellationToken ct = default) =>
        api.SendAsync<PickSheetDto>(new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/slates/{slateId:D}/picks/me"), ct);

    public Task<PlayerApiResult<PickSheetDto>> SavePicksAsync(Guid slateId, IReadOnlyList<PickDto> picks,
        long? expectedVersion, CancellationToken ct = default) =>
        api.SendAsync<PickSheetDto>(new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/slates/{slateId:D}/picks")
        {
            Content = JsonContent.Create(new SavePicksRequest(picks, expectedVersion))
        }, ct);

    public Task<PlayerApiResult<PickSheetDto>> SubmitAsync(Guid slateId, long? expectedVersion,
        CancellationToken ct = default) =>
        api.SendAsync<PickSheetDto>(new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/slates/{slateId:D}/submit")
        {
            Content = JsonContent.Create(new PlayerSubmitRequest(expectedVersion))
        }, ct);

    public Task<PlayerApiResult<LeaderboardDto>> GetLeaderboardAsync(Guid leagueId, int season, int? week,
        CancellationToken ct = default)
    {
        var query = $"?scope=college&season={season}" + (week is null ? "" : $"&week={week.Value}");
        return api.SendAsync<LeaderboardDto>(new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/leagues/{leagueId:D}/leaderboard{query}"), ct);
    }
}

public sealed record PlayerSubmitRequest(long? ExpectedVersion);

public sealed class PlayerLeagueSelectionState(PlayerPortalService portal, IJSRuntime javascript)
{
    private const string StorageKey = "saturdayScout.selectedLeagueId";
    private bool loaded;
    private readonly List<LeagueDto> leagues = [];

    public IReadOnlyList<LeagueDto> Leagues => leagues;
    public Guid? SelectedLeagueId { get; private set; }
    public LeagueDto? SelectedLeague => leagues.FirstOrDefault(x => x.Id == SelectedLeagueId);

    public async Task<PlayerApiResult<IReadOnlyList<LeagueDto>>> RefreshAsync(CancellationToken ct = default)
    {
        Guid? preferred = null;
        try
        {
            var stored = await javascript.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (Guid.TryParse(stored, out var storedId)) preferred = storedId;
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }

        var result = await portal.GetMyLeaguesAsync(ct);
        if (result.Outcome != PlayerApiOutcome.Success || result.Value is null) return result;
        leagues.Clear();
        leagues.AddRange(result.Value);
        if (preferred is { } preferredId && leagues.Any(x => x.Id == preferredId))
            SelectedLeagueId = preferredId;
        else if (SelectedLeagueId is null || leagues.All(x => x.Id != SelectedLeagueId))
            SelectedLeagueId = leagues.FirstOrDefault()?.Id;
        loaded = true;
        return result;
    }

    public async Task<PlayerApiResult<IReadOnlyList<LeagueDto>>> EnsureLoadedAsync(CancellationToken ct = default) =>
        loaded
            ? new(PlayerApiOutcome.Success, leagues.ToArray())
            : await RefreshAsync(ct);

    public bool Select(Guid leagueId)
    {
        if (leagues.All(x => x.Id != leagueId)) return false;
        SelectedLeagueId = leagueId;
        return true;
    }

    public async Task<bool> SelectAsync(Guid leagueId)
    {
        if (!Select(leagueId)) return false;
        try { await javascript.InvokeVoidAsync("localStorage.setItem", StorageKey, leagueId.ToString("D")); }
        catch (JSException) { }
        catch (InvalidOperationException) { }
        return true;
    }
}
