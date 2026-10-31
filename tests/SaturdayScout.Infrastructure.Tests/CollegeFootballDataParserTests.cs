using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.CollegeFootballData;
using Xunit;

namespace SaturdayScout.Infrastructure.Tests;

public sealed class CollegeFootballDataParserTests
{
    internal static ImportedWeekFixture Read() => new(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "teams.json")),
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "schedule.json")),
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "games.json")));

    [Fact]
    public void MapsOnlySupportedGamesAndReportsEverySkippedRecord()
    {
        var fixture = Read();
        var batch = CollegeFootballDataParser.Parse(fixture.Teams, fixture.Schedule, fixture.Games,
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, batch.Games.Count);
        Assert.Equal("CFBD-2", batch.Teams.Single(x => x.ExternalId == "2").Abbreviation);
        Assert.Equal(GameStatus.Scheduled, batch.Games.Single(x => x.ExternalId == "101").Status);
        var final = batch.Games.Single(x => x.ExternalId == "102");
        Assert.Equal(GameStatus.Final, final.Status);
        Assert.Equal((27, 14), (final.HomeScore, final.AwayScore));
        Assert.Contains(batch.Issues, x => x.ExternalId == "103" && x.Reason.Contains("kickoff"));
        Assert.Contains(batch.Issues, x => x.ExternalId == "104" && x.Reason.Contains("scores"));
        Assert.Contains(batch.Issues, x => x.ExternalId == "105" && x.Reason.Contains("Unsupported"));
    }

    [Fact]
    public void DoesNotInferFinalFromScoresAlone()
    {
        var fixture = Read();
        var games = fixture.Games.Replace("\"id\":101,\"completed\":false", "\"id\":101,\"completed\":true");
        var batch = CollegeFootballDataParser.Parse(fixture.Teams, fixture.Schedule, games, DateTimeOffset.UtcNow);
        Assert.Equal(GameStatus.Scheduled, batch.Games.Single(x => x.ExternalId == "101").Status);
        Assert.Contains(batch.Issues, x => x.ExternalId == "101" && x.Reason.Contains("disagree"));
    }
}

internal sealed record ImportedWeekFixture(string Teams, string Schedule, string Games);
