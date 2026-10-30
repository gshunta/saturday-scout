namespace SaturdayScout.Domain;

public enum PickOutcome { Correct, Incorrect, Void, Pending }
public sealed record PickScore(Guid GameId, PickOutcome Outcome, int AwardedPoints);

public static class ConfidenceScoringService
{
    public static PickScore Score(Pick pick, Game game)
    {
        if (pick.GameId != game.Id || pick.ConfidenceRank is < 1 or > 10 ||
            pick.SelectedTeamId != game.HomeTeamId && pick.SelectedTeamId != game.AwayTeamId)
            throw new ArgumentException("Pick does not match the game.", nameof(pick));

        if (game.Status is GameStatus.Canceled or GameStatus.NoContest)
            return new PickScore(game.Id, PickOutcome.Void, 0);
        if (game.Status != GameStatus.Final)
            return new PickScore(game.Id, PickOutcome.Pending, 0);
        if (game.HomeScore is null || game.AwayScore is null)
            throw new InvalidOperationException("A final game requires scores.");
        if (game.HomeScore == game.AwayScore)
            return new PickScore(game.Id, PickOutcome.Void, 0);
        var winner = game.HomeScore > game.AwayScore ? game.HomeTeamId : game.AwayTeamId;
        return pick.SelectedTeamId == winner
            ? new PickScore(game.Id, PickOutcome.Correct, pick.ConfidenceRank)
            : new PickScore(game.Id, PickOutcome.Incorrect, 0);
    }
}
