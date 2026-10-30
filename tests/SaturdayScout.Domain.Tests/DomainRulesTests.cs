using SaturdayScout.Domain;

namespace SaturdayScout.Domain.Tests;

public sealed class DomainRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static (WeeklySlate Slate, List<Game> Games, PickSheet Sheet) Published()
    {
        var slate = new WeeklySlate(Guid.NewGuid(), Guid.NewGuid(), Sport.CollegeFootball, 2026, 9);
        var games = Enumerable.Range(0, 10).Select(i => new Game(Guid.NewGuid(), Sport.CollegeFootball,
            2026, 9, Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(4 + i), "fixture", $"game-{i}")).ToList();
        slate.ReplaceGames(games);
        slate.Publish(games, Now.AddHours(3), Now);
        return (slate, games, new PickSheet(Guid.NewGuid(), slate.Id, Guid.NewGuid()));
    }

    private static List<(Guid GameId, Guid SelectedTeamId, int ConfidenceRank)> CorrectPicks(List<Game> games) =>
        games.Select((g, i) => (g.Id, g.HomeTeamId, i + 1)).ToList();

    [Fact]
    public void CompleteSheetSubmitsAndPerfectScoreIs55()
    {
        var (slate, games, sheet) = Published();
        sheet.ReplaceDraftPicks(slate, games, CorrectPicks(games), Now.AddMinutes(1));
        sheet.Submit(slate, Now.AddMinutes(2));
        foreach (var game in games) game.SetFinalScore(20, 10);

        Assert.Equal(PickSheetStatus.Submitted, sheet.Status);
        Assert.Equal(55, sheet.Picks.Sum(p => ConfidenceScoringService.Score(p, games.Single(g => g.Id == p.GameId)).AwardedPoints));
        Assert.Equal(2, sheet.Version);
        Assert.Throws<InvalidOperationException>(() => sheet.Submit(slate, Now.AddMinutes(3)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void DraftRejectsInvalidRank(int rank)
    {
        var (slate, games, sheet) = Published();
        var picks = CorrectPicks(games);
        picks[0] = (picks[0].GameId, picks[0].SelectedTeamId, rank);
        Assert.Throws<ArgumentException>(() => sheet.ReplaceDraftPicks(slate, games, picks, Now));
    }

    [Fact]
    public void DraftRejectsDuplicateRankGameAndInvalidTeamWithoutMutation()
    {
        var (slate, games, sheet) = Published();
        var original = CorrectPicks(games);
        sheet.ReplaceDraftPicks(slate, games, original, Now);
        var duplicateRank = CorrectPicks(games);
        duplicateRank[1] = (duplicateRank[1].GameId, duplicateRank[1].SelectedTeamId, 1);
        Assert.Throws<ArgumentException>(() => sheet.ReplaceDraftPicks(slate, games, duplicateRank, Now));
        var duplicateGame = CorrectPicks(games);
        duplicateGame[1] = (duplicateGame[0].GameId, duplicateGame[0].SelectedTeamId, 2);
        Assert.Throws<ArgumentException>(() => sheet.ReplaceDraftPicks(slate, games, duplicateGame, Now));
        var invalidTeam = CorrectPicks(games);
        invalidTeam[0] = (invalidTeam[0].GameId, Guid.NewGuid(), 1);
        Assert.Throws<ArgumentException>(() => sheet.ReplaceDraftPicks(slate, games, invalidTeam, Now));
        Assert.Equal(10, sheet.Picks.Count);
        Assert.Equal(1, sheet.Version);
    }

    [Fact]
    public void IncompleteDraftCannotSubmit()
    {
        var (slate, games, sheet) = Published();
        sheet.ReplaceDraftPicks(slate, games, CorrectPicks(games).Take(9).ToList(), Now);
        Assert.Throws<InvalidOperationException>(() => sheet.Submit(slate, Now));
        Assert.Equal(PickSheetStatus.Draft, sheet.Status);
    }

    [Fact]
    public void LockIsExclusiveAndPublishedSlateCannotBeChanged()
    {
        var (slate, games, sheet) = Published();
        Assert.Throws<InvalidOperationException>(() => sheet.ReplaceDraftPicks(slate, games, CorrectPicks(games), slate.LockUtc!.Value));
        Assert.Throws<InvalidOperationException>(() => slate.ReplaceGames(games));
        slate.Lock(slate.LockUtc!.Value);
        Assert.Throws<InvalidOperationException>(() => sheet.Submit(slate, slate.LockUtc!.Value));
    }

    [Fact]
    public void PublishingRequiresTenMatchingGamesAndValidLock()
    {
        var slate = new WeeklySlate(Guid.NewGuid(), Guid.NewGuid(), Sport.Nfl, 2026, 8);
        var game = new Game(Guid.NewGuid(), Sport.Nfl, 2026, 8, Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(2), "fixture", "one");
        slate.ReplaceGames([game]);
        Assert.Throws<InvalidOperationException>(() => slate.Publish([game], Now.AddHours(1), Now));
        Assert.Throws<ArgumentException>(() => slate.ReplaceGames([game, game]));
        var wrongWeek = new Game(Guid.NewGuid(), Sport.Nfl, 2026, 9, Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(2), "fixture", "two");
        Assert.Throws<ArgumentException>(() => slate.ReplaceGames([wrongWeek]));
    }

    [Fact]
    public void ScoringDistinguishesPendingIncorrectVoidAndCorrect()
    {
        var (_, games, sheet) = Published();
        var game = games[0];
        var homePick = new Pick(sheet.Id, game.Id, game.HomeTeamId, 10);
        Assert.Equal(new PickScore(game.Id, PickOutcome.Pending, 0), ConfidenceScoringService.Score(homePick, game));
        game.SetFinalScore(10, 20);
        Assert.Equal(new PickScore(game.Id, PickOutcome.Incorrect, 0), ConfidenceScoringService.Score(homePick, game));
        game.SetFinalScore(20, 10);
        Assert.Equal(new PickScore(game.Id, PickOutcome.Correct, 10), ConfidenceScoringService.Score(homePick, game));
        Assert.Equal(ConfidenceScoringService.Score(homePick, game), ConfidenceScoringService.Score(homePick, game));
        game.SetFinalScore(10, 10);
        Assert.Equal(new PickScore(game.Id, PickOutcome.Void, 0), ConfidenceScoringService.Score(homePick, game));
        games[1].SetStatus(GameStatus.Canceled);
        Assert.Equal(PickOutcome.Void, ConfidenceScoringService.Score(new Pick(sheet.Id, games[1].Id, games[1].AwayTeamId, 1), games[1]).Outcome);
        games[2].SetStatus(GameStatus.NoContest);
        Assert.Equal(PickOutcome.Void, ConfidenceScoringService.Score(new Pick(sheet.Id, games[2].Id, games[2].AwayTeamId, 1), games[2]).Outcome);
    }

    [Fact]
    public void InvalidIdsTimesAndScoresAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new WeeklySlate(Guid.Empty, Guid.NewGuid(), Sport.Nfl, 2026, 1));
        Assert.Throws<ArgumentException>(() => new Game(Guid.NewGuid(), Sport.Nfl, 2026, 1,
            Guid.NewGuid(), Guid.NewGuid(), Now.ToOffset(TimeSpan.FromHours(-4)), "fixture", "bad-time"));
        var game = Published().Games[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => game.SetFinalScore(-1, 0));
        Assert.Throws<ArgumentException>(() => game.SetStatus(GameStatus.Final));
    }
}
