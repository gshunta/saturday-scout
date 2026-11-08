using SaturdayScout.Application;
using SaturdayScout.Player;
using Xunit;

namespace SaturdayScout.Player.Tests;

public sealed class PlayerPickDraftRulesTests
{
    private static PlayerSlateDto Slate() => new(Guid.NewGuid(), Guid.NewGuid(), 2026, 10,
        "Published", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow,
        Enumerable.Range(0, 10).Select(i => new PlayerSlateGameDto(i + 1, Guid.NewGuid(), Guid.NewGuid(),
            $"Home {i}", $"H{i}", Guid.NewGuid(), $"Away {i}", $"A{i}", DateTimeOffset.UtcNow.AddDays(1),
            "Scheduled", null, null)).ToArray());

    [Fact]
    public void TenDistinctRanksAndWinnersAreReadyAndMapToApiPicks()
    {
        var slate = Slate();
        var rows = slate.Games.Select((game, i) =>
        {
            var row = new PlayerPickSelection(game) { SelectedTeamId = game.HomeTeamId, ConfidenceRank = i + 1 };
            return row;
        }).ToArray();

        var validation = PlayerPickDraftRules.Validate(rows);

        Assert.True(validation.IsReadyToSubmit);
        Assert.Equal(10, PlayerPickDraftRules.ToPicks(rows).Count);
    }

    [Fact]
    public void DuplicateAndOutOfRangeRanksAreNotReady()
    {
        var slate = Slate();
        var rows = slate.Games.Select((game, i) => new PlayerPickSelection(game)
        {
            SelectedTeamId = game.AwayTeamId,
            ConfidenceRank = i switch { 8 => 11, 9 => 1, _ => i + 1 }
        }).ToArray();
        rows[0].ConfidenceRank = 1;

        var validation = PlayerPickDraftRules.Validate(rows);

        Assert.False(validation.IsReadyToSubmit);
        Assert.Contains(1, validation.DuplicateRanks);
        Assert.Contains(9, validation.MissingRanks);
        Assert.Contains(10, validation.MissingRanks);
    }

    [Fact]
    public void IncompleteDraftCanBeRestoredWithoutInventingPicks()
    {
        var slate = Slate();
        var savedGame = slate.Games[2];
        var sheet = new PickSheetDto(Guid.NewGuid(), slate.Id, "Draft", null, 7,
            [new PickDto(savedGame.GameId, savedGame.AwayTeamId, 4)]);

        var restored = PlayerPickDraftRules.Restore(slate, sheet);

        Assert.Equal(10, restored.Count);
        Assert.Equal(savedGame.AwayTeamId, restored[2].SelectedTeamId);
        Assert.Equal(4, restored[2].ConfidenceRank);
        Assert.Null(restored[0].SelectedTeamId);
        Assert.Null(restored[0].ConfidenceRank);
    }

    [Fact]
    public void DraftSaveAllowsAnIncompleteSheetWhenEnteredPicksAreValid()
    {
        var slate = Slate();
        var rows = PlayerPickDraftRules.Restore(slate, null).ToArray();
        rows[0].SelectedTeamId = rows[0].Game.HomeTeamId;
        rows[0].ConfidenceRank = 1;
        rows[1].SelectedTeamId = rows[1].Game.AwayTeamId;
        rows[1].ConfidenceRank = 2;

        var validation = PlayerPickDraftRules.Validate(rows);

        Assert.False(validation.IsReadyToSubmit);
        Assert.Empty(PlayerPickDraftRules.DraftSaveIssues(validation));
        Assert.Equal(2, PlayerPickDraftRules.ToPicks(rows).Count);
    }

    [Fact]
    public void DuplicateConfidenceShowsActionableSaveAndReviewErrors()
    {
        var slate = Slate();
        var rows = slate.Games.Select((game, i) => new PlayerPickSelection(game)
        {
            SelectedTeamId = game.HomeTeamId,
            ConfidenceRank = i == 1 ? 1 : i + 1
        }).ToArray();
        var validation = PlayerPickDraftRules.Validate(rows);

        var saveIssues = PlayerPickDraftRules.DraftSaveIssues(validation);
        var reviewIssues = PlayerPickDraftRules.SubmissionIssues(validation, rows.Length,
            hasUnsavedChanges: true, hasSheet: true, hasConflict: false);

        Assert.Contains(saveIssues, issue => issue.Contains("unique", StringComparison.OrdinalIgnoreCase) && issue.Contains("1"));
        Assert.Contains(reviewIssues, issue => issue.Contains("Repeated value(s): 1", StringComparison.Ordinal));
        Assert.Contains(reviewIssues, issue => issue.Contains("Missing value(s): 2", StringComparison.Ordinal));
        Assert.Contains(reviewIssues, issue => issue.Contains("Save your latest draft changes", StringComparison.Ordinal));
    }
}
