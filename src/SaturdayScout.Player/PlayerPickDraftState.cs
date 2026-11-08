using SaturdayScout.Application;

namespace SaturdayScout.Player;

public sealed class PlayerPickSelection(PlayerSlateGameDto game)
{
    public PlayerSlateGameDto Game { get; } = game;
    public Guid? SelectedTeamId { get; set; }
    public int? ConfidenceRank { get; set; }
}

public sealed record PlayerPickValidation(
    int UnansweredGames,
    int IncompleteRows,
    IReadOnlyList<int> DuplicateRanks,
    IReadOnlyList<int> MissingRanks,
    bool IsReadyToSubmit);

public static class PlayerPickDraftRules
{
    public static PlayerPickValidation Validate(IReadOnlyCollection<PlayerPickSelection> selections)
    {
        var completeRows = selections.Count(x => x.SelectedTeamId is not null && x.ConfidenceRank is not null);
        var incompleteRows = selections.Count(x => (x.SelectedTeamId is null) != (x.ConfidenceRank is null));
        var ranks = selections.Where(x => x.ConfidenceRank is not null).Select(x => x.ConfidenceRank!.Value).ToArray();
        var duplicates = ranks.GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).Order().ToArray();
        var missingRanks = Enumerable.Range(1, 10).Except(ranks).ToArray();
        var ready = selections.Count == 10 && completeRows == 10 && incompleteRows == 0 &&
            duplicates.Length == 0 && ranks.Distinct().Count() == 10 && ranks.All(x => x is >= 1 and <= 10);
        return new PlayerPickValidation(selections.Count - completeRows, incompleteRows,
            duplicates, missingRanks, ready);
    }

    public static IReadOnlyList<PickDto> ToPicks(IReadOnlyCollection<PlayerPickSelection> selections) =>
        selections.Where(x => x.SelectedTeamId is not null && x.ConfidenceRank is not null)
            .Select(x => new PickDto(x.Game.GameId, x.SelectedTeamId!.Value, x.ConfidenceRank!.Value))
            .ToArray();

    public static IReadOnlyList<string> DraftSaveIssues(PlayerPickValidation validation)
    {
        var issues = new List<string>();
        if (validation.IncompleteRows > 0)
            issues.Add("For each game, choose both a winner and confidence value, or clear both selections.");
        if (validation.DuplicateRanks.Count > 0)
            issues.Add($"Confidence values must be unique. Repeated value(s): {FormatRanks(validation.DuplicateRanks)}.");
        return issues;
    }

    public static IReadOnlyList<string> SubmissionIssues(PlayerPickValidation validation,
        int gameCount, bool hasUnsavedChanges, bool hasSheet, bool hasConflict)
    {
        var issues = new List<string>();
        if (hasConflict) issues.Add("Resolve the server version conflict before reviewing submission.");
        if (hasUnsavedChanges) issues.Add("Save your latest draft changes before reviewing submission.");
        if (!hasSheet) issues.Add("Save a draft before submitting picks.");
        if (gameCount != 10) issues.Add("A submission requires exactly 10 games.");
        if (validation.UnansweredGames > 0)
            issues.Add($"Choose a winner and confidence value for all games; {validation.UnansweredGames} game(s) remain incomplete.");
        if (validation.IncompleteRows > 0)
            issues.Add("Complete or clear every partially filled game.");
        if (validation.DuplicateRanks.Count > 0)
            issues.Add($"Confidence values must be unique. Repeated value(s): {FormatRanks(validation.DuplicateRanks)}.");
        if (validation.MissingRanks.Count > 0)
            issues.Add($"Assign each confidence value from 1 through 10 once. Missing value(s): {FormatRanks(validation.MissingRanks)}.");
        return issues;
    }

    private static string FormatRanks(IEnumerable<int> ranks) => string.Join(", ", ranks.Order());

    public static IReadOnlyList<PlayerPickSelection> Restore(PlayerSlateDto slate, PickSheetDto? sheet)
    {
        var saved = sheet?.Picks.ToDictionary(x => x.GameId) ?? [];
        return slate.Games.OrderBy(x => x.DisplayOrder).Select(game =>
        {
            var selection = new PlayerPickSelection(game);
            if (saved.TryGetValue(game.GameId, out var pick))
            {
                selection.SelectedTeamId = pick.SelectedTeamId;
                selection.ConfidenceRank = pick.ConfidenceRank;
            }
            return selection;
        }).ToArray();
    }
}
