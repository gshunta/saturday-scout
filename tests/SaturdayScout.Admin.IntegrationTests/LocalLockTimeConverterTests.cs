using SaturdayScout.Admin;
using SaturdayScout.Domain;
using Xunit;

namespace SaturdayScout.Admin.IntegrationTests;

public sealed class LocalLockTimeConverterTests
{
    private const string Detroit = "America/Detroit";

    [Theory]
    [InlineData("2026-11-15T15:42", "2026-11-15T20:42:00+00:00")]
    [InlineData("2026-11-15T15:42:00", "2026-11-15T20:42:00+00:00")]
    [InlineData("2026-11-15T15:42:00.125", "2026-11-15T20:42:00.125+00:00")]
    public void ParsesValidHtmlDateTimeLocalValuesAsBrowserLocalTime(string input, string expectedUtc)
    {
        var converted = LocalLockTimeConverter.ConvertToUtc(input, Detroit);

        Assert.Equal(DateTimeOffset.Parse(expectedUtc), converted.LockUtc);
        Assert.False(converted.IsAmbiguous);
    }

    [Fact]
    public void NormalizesRawHtmlValueWithoutRetainingLocaleFormatting()
    {
        Assert.Equal("2026-11-15T15:42:00", LocalLockTimeConverter.NormalizeBrowserValue("2026-11-15T15:42"));
        Assert.Equal("11/15/2026 03:42 PM", LocalLockTimeConverter.NormalizeBrowserValue(" 11/15/2026 03:42 PM "));
        Assert.Equal(string.Empty, LocalLockTimeConverter.NormalizeBrowserValue(" "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("11/15/2026 03:42 PM")]
    [InlineData("2026-11-15T15:42Z")]
    public void RejectsEmptyLocalizedOrOffsetBearingInput(string? input)
    {
        Assert.Throws<ArgumentException>(() => LocalLockTimeConverter.ConvertToUtc(input, Detroit));
    }

    [Fact]
    public void UsesEasternStandardOffsetForDetroitNovember()
    {
        var converted = LocalLockTimeConverter.ConvertToUtc("2026-11-15T15:42", Detroit);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 20, 42, 0, TimeSpan.Zero), converted.LockUtc);
    }

    [Fact]
    public void UsesEasternDaylightOffsetForDetroitJuly()
    {
        var converted = LocalLockTimeConverter.ConvertToUtc("2026-07-15T15:42", Detroit);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 19, 42, 0, TimeSpan.Zero), converted.LockUtc);
    }

    [Fact]
    public void RejectsNonexistentLocalTimeDuringSpringForward()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            LocalLockTimeConverter.ConvertToUtc("2026-03-08T02:30", Detroit));
        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public void RequiresAnExplicitChoiceAndMapsBothFallBackOccurrencesToTheirUtcInstants()
    {
        var localValue = "2026-11-01T01:30";
        var occurrences = LocalLockTimeConverter.GetAmbiguousUtcOccurrences(localValue, Detroit);

        Assert.Equal([
            new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)
        ], occurrences);
        Assert.Throws<ArgumentException>(() => LocalLockTimeConverter.ConvertToUtc(localValue, Detroit));

        var earlier = LocalLockTimeConverter.ConvertToUtc(localValue, Detroit, AmbiguousTimeOccurrence.Earlier);
        var later = LocalLockTimeConverter.ConvertToUtc(localValue, Detroit, AmbiguousTimeOccurrence.Later);
        Assert.True(earlier.IsAmbiguous);
        Assert.True(later.IsAmbiguous);
        Assert.Equal(occurrences[0], earlier.LockUtc);
        Assert.Equal(occurrences[1], later.LockUtc);
    }

    [Fact]
    public void ConvertsStoredUtcLockBackToBrowserLocalInputAndDisplaysTheDisambiguatingOffset()
    {
        var winterUtc = new DateTimeOffset(2026, 11, 15, 20, 42, 0, TimeSpan.Zero);
        Assert.Equal("2026-11-15T15:42:00", LocalLockTimeConverter.ToLocalInput(winterUtc, Detroit));
        Assert.Equal("2026-11-15 15:42:00 -05:00", LocalLockTimeConverter.FormatLocal(winterUtc, Detroit));

        var firstFallBackOccurrence = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var secondFallBackOccurrence = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        Assert.Equal("2026-11-01T01:30:00", LocalLockTimeConverter.ToLocalInput(firstFallBackOccurrence, Detroit));
        Assert.Equal("2026-11-01 01:30:00 -04:00", LocalLockTimeConverter.FormatLocal(firstFallBackOccurrence, Detroit));
        Assert.Equal("2026-11-01 01:30:00 -05:00", LocalLockTimeConverter.FormatLocal(secondFallBackOccurrence, Detroit));

        var precision = new DateTimeOffset(2026, 11, 15, 20, 42, 0, TimeSpan.Zero).AddTicks(1250000);
        Assert.Equal("2026-11-15T15:42:00.125", LocalLockTimeConverter.ToLocalInput(precision, Detroit));
        Assert.Equal("2026-11-15 15:42:00.125 -05:00", LocalLockTimeConverter.FormatLocal(precision, Detroit));
    }

    [Fact]
    public void DefaultLocalInputRoundTripsToTheSameUtcMinuteBeforeEarliestKickoff()
    {
        var earliestKickoff = new DateTimeOffset(2026, 11, 16, 5, 54, 29, TimeSpan.Zero).AddTicks(6105410);

        var input = LocalLockTimeConverter.CreateDefaultLocalInput(earliestKickoff, Detroit);
        var converted = LocalLockTimeConverter.ConvertToUtc(input, Detroit);

        Assert.Equal(new DateTimeOffset(2026, 11, 16, 5, 53, 0, TimeSpan.Zero), converted.LockUtc);
        Assert.True(converted.LockUtc < earliestKickoff);
    }

    [Fact]
    public void ConvertedUtcStillUsesExistingFutureAndEarliestKickoffPublicationRules()
    {
        var now = new DateTimeOffset(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);
        var slate = new WeeklySlate(Guid.NewGuid(), Guid.NewGuid(), Sport.CollegeFootball, 2026, 12);
        var games = Enumerable.Range(0, 10).Select(index => new Game(Guid.NewGuid(), Sport.CollegeFootball,
            2026, 12, Guid.NewGuid(), Guid.NewGuid(), now.AddHours(8 + index), "fixture", $"game-{index}"))
            .ToArray();
        slate.ReplaceGames(games);

        var past = LocalLockTimeConverter.ConvertToUtc("2026-11-15T09:30", Detroit).LockUtc;
        Assert.True(past < now);
        Assert.Throws<InvalidOperationException>(() => slate.Publish(games, past, now));

        var afterEarliest = LocalLockTimeConverter.ConvertToUtc("2026-11-15T19:01", Detroit).LockUtc;
        Assert.Throws<InvalidOperationException>(() => slate.Publish(games, afterEarliest, now));

        var safe = LocalLockTimeConverter.ConvertToUtc("2026-11-15T11:30", Detroit).LockUtc;
        Assert.Equal(now.AddHours(1.5), safe);
        slate.Publish(games, safe, now);
        Assert.Equal(safe, slate.LockUtc);
    }
}
