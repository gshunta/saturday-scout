using System.Globalization;

namespace SaturdayScout.Admin;

public sealed record LocalLockTimeConversion(DateTimeOffset LockUtc, bool IsAmbiguous);

public enum AmbiguousTimeOccurrence
{
    Earlier,
    Later
}

/// <summary>Converts a browser datetime-local value using the browser-reported IANA timezone.</summary>
public static class LocalLockTimeConverter
{
    private static readonly string[] HtmlDateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
    ];

    public static LocalLockTimeConversion ConvertToUtc(string? localValue, string? timeZoneId,
        AmbiguousTimeOccurrence? ambiguousOccurrence = null)
    {
        var (parsed, zone) = ParseLocalTime(localValue, timeZoneId);
        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(parsed))
            throw new ArgumentException("This local time does not exist because clocks move forward. Choose another time.", nameof(localValue));

        if (zone.IsAmbiguousTime(parsed))
        {
            if (ambiguousOccurrence is null)
                throw new ArgumentException("This local time occurs twice. Choose the first or second occurrence before continuing.", nameof(localValue));

            var instants = zone.GetAmbiguousTimeOffsets(parsed)
                .Select(offset => new DateTimeOffset(parsed, offset).ToUniversalTime())
                .OrderBy(instant => instant)
                .ToArray();
            return new LocalLockTimeConversion(
                instants[ambiguousOccurrence == AmbiguousTimeOccurrence.Earlier ? 0 : 1], true);
        }

        var offset = zone.GetUtcOffset(parsed);
        return new LocalLockTimeConversion(new DateTimeOffset(parsed, offset).ToUniversalTime(), false);
    }

    public static IReadOnlyList<DateTimeOffset> GetAmbiguousUtcOccurrences(string? localValue, string? timeZoneId)
    {
        var (parsed, zone) = ParseLocalTime(localValue, timeZoneId);
        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(parsed))
            throw new ArgumentException("This local time does not exist because clocks move forward. Choose another time.", nameof(localValue));
        if (!zone.IsAmbiguousTime(parsed)) return [];

        return zone.GetAmbiguousTimeOffsets(parsed)
            .Select(offset => new DateTimeOffset(parsed, offset).ToUniversalTime())
            .OrderBy(instant => instant)
            .ToArray();
    }

    public static string ToLocalInput(DateTimeOffset utcValue, string timeZoneId)
    {
        var local = TimeZoneInfo.ConvertTime(utcValue.ToUniversalTime(), FindTimeZone(timeZoneId));
        var value = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fractionalTicks = local.Ticks % TimeSpan.TicksPerSecond;
        return fractionalTicks == 0
            ? value
            : $"{value}.{fractionalTicks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0')}";
    }

    public static string FormatLocal(DateTimeOffset utcValue, string timeZoneId)
    {
        var local = TimeZoneInfo.ConvertTime(utcValue.ToUniversalTime(), FindTimeZone(timeZoneId));
        var value = local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var fractionalTicks = local.Ticks % TimeSpan.TicksPerSecond;
        if (fractionalTicks != 0)
            value += $".{fractionalTicks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0')}";
        return value + local.ToString(" zzz", CultureInfo.InvariantCulture);
    }

    public static string CreateDefaultLocalInput(DateTimeOffset earliestKickoffUtc, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var beforeKickoff = earliestKickoffUtc.ToUniversalTime().AddMinutes(-1);
        var candidate = new DateTimeOffset(beforeKickoff.Year, beforeKickoff.Month, beforeKickoff.Day,
            beforeKickoff.Hour, beforeKickoff.Minute, 0, TimeSpan.Zero);

        // The control has minute precision. Preserve a round-trip-safe instant before kickoff,
        // including when the initial local value falls in the repeated hour at DST fall-back.
        for (var attempt = 0; attempt < 180; attempt++, candidate = candidate.AddMinutes(-1))
        {
            var local = TimeZoneInfo.ConvertTime(candidate, zone).DateTime;
            var value = local.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
            var converted = ConvertToUtc(value, timeZoneId, AmbiguousTimeOccurrence.Later);
            if (converted.LockUtc == candidate) return value;
        }

        throw new InvalidOperationException("A safe local default lock time could not be determined.");
    }

    public static string NormalizeBrowserValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return DateTime.TryParseExact(value.Trim(), HtmlDateTimeFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : value.Trim();
    }

    private static (DateTime Parsed, TimeZoneInfo Zone) ParseLocalTime(string? localValue, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(localValue) ||
            !DateTime.TryParseExact(localValue.Trim(), HtmlDateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            throw new ArgumentException("Choose a valid local date and time using the browser picker.", nameof(localValue));
        return (parsed, FindTimeZone(timeZoneId));
    }

    private static TimeZoneInfo FindTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("The browser timezone is not available.", nameof(timeZoneId));
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException exception)
        { throw new ArgumentException("The browser timezone could not be resolved.", nameof(timeZoneId), exception); }
        catch (InvalidTimeZoneException exception)
        { throw new ArgumentException("The browser timezone data is invalid.", nameof(timeZoneId), exception); }
    }
}
