namespace SaturdayScout.Domain;

public enum Sport { CollegeFootball, Nfl }
public enum GameStatus { Scheduled, InProgress, Final, Postponed, Canceled, NoContest }
public enum MembershipRole { Member, Commissioner }
public enum SlateStatus { Draft, Published, Locked, Finalized }
public enum PickSheetStatus { Draft, Submitted }

public sealed class Team
{
    public Guid Id { get; }
    public Sport Sport { get; }
    public string Name { get; private set; }
    public string Abbreviation { get; private set; }
    public string Provider { get; }
    public string ExternalId { get; }

    public Team(Guid id, Sport sport, string name, string abbreviation, string provider, string externalId)
    {
        DomainGuard.Id(id);
        DomainGuard.Sport(sport);
        Id = id;
        Sport = sport;
        Name = DomainGuard.Text(name);
        Abbreviation = DomainGuard.Text(abbreviation);
        Provider = DomainGuard.Text(provider);
        ExternalId = DomainGuard.Text(externalId);
    }

    public void UpdateDetails(string name, string abbreviation)
    {
        Name = DomainGuard.Text(name);
        Abbreviation = DomainGuard.Text(abbreviation);
    }
}

public sealed class Game
{
    public Guid Id { get; }
    public Sport Sport { get; }
    public int Season { get; }
    public int Week { get; }
    public Guid HomeTeamId { get; }
    public Guid AwayTeamId { get; }
    public DateTimeOffset KickoffUtc { get; private set; }
    public GameStatus Status { get; private set; }
    public int? HomeScore { get; private set; }
    public int? AwayScore { get; private set; }
    public string Provider { get; }
    public string ExternalId { get; }
    public string? SourceStatus { get; private set; }
    public DateTimeOffset? SourceRetrievedUtc { get; private set; }

    public Game(Guid id, Sport sport, int season, int week, Guid homeTeamId, Guid awayTeamId,
        DateTimeOffset kickoffUtc, string provider, string externalId)
    {
        DomainGuard.Id(id);
        DomainGuard.Sport(sport);
        DomainGuard.SeasonWeek(season, week);
        DomainGuard.Id(homeTeamId);
        DomainGuard.Id(awayTeamId);
        if (homeTeamId == awayTeamId) throw new ArgumentException("Teams must differ.");
        DomainGuard.Utc(kickoffUtc);
        Id = id;
        Sport = sport;
        Season = season;
        Week = week;
        HomeTeamId = homeTeamId;
        AwayTeamId = awayTeamId;
        KickoffUtc = kickoffUtc;
        Provider = DomainGuard.Text(provider);
        ExternalId = DomainGuard.Text(externalId);
        Status = GameStatus.Scheduled;
    }

    public void SetKickoff(DateTimeOffset kickoffUtc)
    {
        DomainGuard.Utc(kickoffUtc);
        if (Status is GameStatus.Final or GameStatus.Canceled or GameStatus.NoContest)
            throw new InvalidOperationException("A concluded game cannot be rescheduled.");
        KickoffUtc = kickoffUtc;
    }

    public void SetStatus(GameStatus status)
    {
        if (!Enum.IsDefined(status) || status == GameStatus.Final)
            throw new ArgumentException("Use SetFinalScore for a final game.", nameof(status));
        if (Status == GameStatus.Final) throw new InvalidOperationException("A final score requires an explicit correction.");
        Status = status;
        HomeScore = AwayScore = null;
    }

    public void SetFinalScore(int homeScore, int awayScore)
    {
        if (homeScore < 0 || awayScore < 0) throw new ArgumentOutOfRangeException(nameof(homeScore), "Scores must be nonnegative.");
        if (Status is GameStatus.Canceled or GameStatus.NoContest)
            throw new InvalidOperationException("A void game cannot be finalized.");
        HomeScore = homeScore;
        AwayScore = awayScore;
        Status = GameStatus.Final;
    }

    public void RecordSource(string rawStatus, DateTimeOffset retrievedUtc)
    {
        DomainGuard.Utc(retrievedUtc);
        if (SourceRetrievedUtc is not null && retrievedUtc < SourceRetrievedUtc)
            throw new InvalidOperationException("Older source data cannot replace newer source metadata.");
        SourceStatus = DomainGuard.Text(rawStatus);
        SourceRetrievedUtc = retrievedUtc;
    }
}

public sealed class User
{
    public Guid Id { get; }
    public string DisplayName { get; }
    public string ExternalSubjectId { get; }
    public string? Email { get; }

    public User(Guid id, string displayName, string externalSubjectId, string? email = null)
    {
        DomainGuard.Id(id);
        Id = id;
        DisplayName = DomainGuard.Text(displayName);
        ExternalSubjectId = DomainGuard.Text(externalSubjectId);
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }
}

public sealed class League
{
    public Guid Id { get; }
    public string Name { get; }
    public Guid CommissionerUserId { get; }
    public string InviteCodeHash { get; }
    public DateTimeOffset InviteExpiresUtc { get; }

    public League(Guid id, string name, Guid commissionerUserId, string inviteCodeHash, DateTimeOffset inviteExpiresUtc)
    {
        DomainGuard.Id(id);
        DomainGuard.Id(commissionerUserId);
        DomainGuard.Utc(inviteExpiresUtc);
        Id = id;
        Name = DomainGuard.Text(name);
        CommissionerUserId = commissionerUserId;
        InviteCodeHash = DomainGuard.Text(inviteCodeHash);
        InviteExpiresUtc = inviteExpiresUtc;
    }
}

public sealed class LeagueMember
{
    public Guid LeagueId { get; }
    public Guid UserId { get; }
    public MembershipRole Role { get; }
    public DateTimeOffset JoinedUtc { get; }

    public LeagueMember(Guid leagueId, Guid userId, MembershipRole role, DateTimeOffset joinedUtc)
    {
        DomainGuard.Id(leagueId);
        DomainGuard.Id(userId);
        DomainGuard.Utc(joinedUtc);
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        LeagueId = leagueId;
        UserId = userId;
        Role = role;
        JoinedUtc = joinedUtc;
    }
}

public sealed record SlateGame(Guid SlateId, Guid GameId, int DisplayOrder);
public sealed record Pick(Guid SheetId, Guid GameId, Guid SelectedTeamId, int ConfidenceRank);

public sealed class WeeklySlate
{
    private readonly List<SlateGame> _games = [];
    public Guid Id { get; }
    public Guid LeagueId { get; }
    public Sport Sport { get; }
    public int Season { get; }
    public int Week { get; }
    public SlateStatus Status { get; private set; } = SlateStatus.Draft;
    public DateTimeOffset? LockUtc { get; private set; }
    public DateTimeOffset? PublishedUtc { get; private set; }
    public IReadOnlyList<SlateGame> Games => _games.AsReadOnly();

    public WeeklySlate(Guid id, Guid leagueId, Sport sport, int season, int week)
    {
        DomainGuard.Id(id);
        DomainGuard.Id(leagueId);
        DomainGuard.Sport(sport);
        DomainGuard.SeasonWeek(season, week);
        Id = id;
        LeagueId = leagueId;
        Sport = sport;
        Season = season;
        Week = week;
    }

    public void ReplaceGames(IEnumerable<Game> games)
    {
        if (Status != SlateStatus.Draft) throw new InvalidOperationException("Only a draft slate can change games.");
        var selected = games?.ToList() ?? throw new ArgumentNullException(nameof(games));
        if (selected.Count > 10 || selected.Any(g => g is null) || selected.Select(g => g.Id).Distinct().Count() != selected.Count)
            throw new ArgumentException("A slate may contain at most 10 distinct games.", nameof(games));
        if (selected.Any(g => g.Sport != Sport || g.Season != Season || g.Week != Week || g.Status is GameStatus.Canceled or GameStatus.NoContest or GameStatus.Final))
            throw new ArgumentException("Games must match the slate and be eligible for selection.", nameof(games));
        _games.Clear();
        _games.AddRange(selected.Select((game, index) => new SlateGame(Id, game.Id, index + 1)));
    }

    public void Publish(IReadOnlyCollection<Game> games, DateTimeOffset lockUtc, DateTimeOffset nowUtc)
    {
        if (Status != SlateStatus.Draft) throw new InvalidOperationException("Only a draft slate can be published.");
        DomainGuard.Utc(lockUtc);
        DomainGuard.Utc(nowUtc);
        if (_games.Count != 10 || games.Count != 10 || games.Select(g => g.Id).Distinct().Count() != 10 ||
            !_games.Select(g => g.GameId).ToHashSet().SetEquals(games.Select(g => g.Id)))
            throw new InvalidOperationException("A published slate requires exactly its 10 distinct games.");
        if (games.Any(g => g.Sport != Sport || g.Season != Season || g.Week != Week || g.Status is GameStatus.Canceled or GameStatus.NoContest or GameStatus.Final))
            throw new InvalidOperationException("Slate games must match sport, season, and week and be eligible.");
        if (lockUtc > games.Min(g => g.KickoffUtc) || lockUtc <= nowUtc)
            throw new InvalidOperationException("Lock must be in the future and no later than the first kickoff.");
        LockUtc = lockUtc;
        PublishedUtc = nowUtc;
        Status = SlateStatus.Published;
    }

    public void Lock(DateTimeOffset nowUtc)
    {
        DomainGuard.Utc(nowUtc);
        if (Status != SlateStatus.Published || nowUtc < LockUtc)
            throw new InvalidOperationException("Slate is not ready to lock.");
        Status = SlateStatus.Locked;
    }

    public void FinalizeSlate(IReadOnlyCollection<Game> games)
    {
        if (Status != SlateStatus.Locked || games.Count != 10 ||
            !_games.Select(g => g.GameId).ToHashSet().SetEquals(games.Select(g => g.Id)) ||
            games.Any(g => g.Status is not (GameStatus.Final or GameStatus.Canceled or GameStatus.NoContest)))
            throw new InvalidOperationException("Every slate game must be concluded before finalization.");
        Status = SlateStatus.Finalized;
    }
}

public sealed class PickSheet
{
    private readonly List<Pick> _picks = [];
    public Guid Id { get; }
    public Guid SlateId { get; }
    public Guid UserId { get; }
    public PickSheetStatus Status { get; private set; } = PickSheetStatus.Draft;
    public DateTimeOffset? SubmittedUtc { get; private set; }
    public long Version { get; private set; }
    public IReadOnlyList<Pick> Picks => _picks.AsReadOnly();

    public PickSheet(Guid id, Guid slateId, Guid userId)
    {
        DomainGuard.Id(id);
        DomainGuard.Id(slateId);
        DomainGuard.Id(userId);
        Id = id;
        SlateId = slateId;
        UserId = userId;
    }

    public void ReplaceDraftPicks(WeeklySlate slate, IReadOnlyCollection<Game> games,
        IEnumerable<(Guid GameId, Guid SelectedTeamId, int ConfidenceRank)> picks, DateTimeOffset nowUtc)
    {
        EnsureEditable(slate, nowUtc);
        var proposed = picks?.ToList() ?? throw new ArgumentNullException(nameof(picks));
        if (proposed.Count > 10 || proposed.Select(p => p.GameId).Distinct().Count() != proposed.Count ||
            proposed.Select(p => p.ConfidenceRank).Distinct().Count() != proposed.Count ||
            proposed.Any(p => p.ConfidenceRank is < 1 or > 10))
            throw new ArgumentException("Draft picks require distinct games and ranks from 1 to 10.", nameof(picks));
        var selectedGames = slate.Games.Select(g => g.GameId).ToHashSet();
        var gameMap = games.ToDictionary(g => g.Id);
        foreach (var pick in proposed)
        {
            if (!selectedGames.Contains(pick.GameId) || !gameMap.TryGetValue(pick.GameId, out var game) ||
                pick.SelectedTeamId != game.HomeTeamId && pick.SelectedTeamId != game.AwayTeamId)
                throw new ArgumentException("Pick must select a team in a slate game.", nameof(picks));
        }
        _picks.Clear();
        _picks.AddRange(proposed.Select(p => new Pick(Id, p.GameId, p.SelectedTeamId, p.ConfidenceRank)));
        Version++;
    }

    public void Submit(WeeklySlate slate, DateTimeOffset nowUtc)
    {
        EnsureEditable(slate, nowUtc);
        if (slate.Games.Count != 10 || _picks.Count != 10 ||
            !_picks.Select(p => p.GameId).ToHashSet().SetEquals(slate.Games.Select(g => g.GameId)) ||
            !_picks.Select(p => p.ConfidenceRank).ToHashSet().SetEquals(Enumerable.Range(1, 10)))
            throw new InvalidOperationException("Submission requires one pick per game and ranks 1 through 10.");
        Status = PickSheetStatus.Submitted;
        SubmittedUtc = nowUtc;
        Version++;
    }

    private void EnsureEditable(WeeklySlate slate, DateTimeOffset nowUtc)
    {
        DomainGuard.Utc(nowUtc);
        if (slate.Id != SlateId || slate.Status != SlateStatus.Published ||
            slate.LockUtc is null || nowUtc >= slate.LockUtc || Status != PickSheetStatus.Draft)
            throw new InvalidOperationException("Sheet is not editable.");
    }
}

internal static class DomainGuard
{
    public static void Id(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("ID cannot be empty.");
    }
    public static string Text(string value) => !string.IsNullOrWhiteSpace(value)
        ? value.Trim() : throw new ArgumentException("Text cannot be blank.");
    public static void Utc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Instant must have a UTC offset.");
    }
    public static void Sport(Sport sport)
    {
        if (!Enum.IsDefined(sport)) throw new ArgumentOutOfRangeException(nameof(sport));
    }
    public static void SeasonWeek(int season, int week)
    {
        if (season < 1900 || season > 9999 || week < 1 || week > 30)
            throw new ArgumentOutOfRangeException(nameof(week), "Season or week is out of range.");
    }
}
