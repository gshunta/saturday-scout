using System.Globalization;
using Microsoft.JSInterop;

namespace SaturdayScout.Player;

public static class PlayerLocalTimeFormatter
{
    public static string Format(DateTimeOffset? value, TimeZoneInfo? browserTimeZone, string unavailableText)
    {
        if (value is null) return unavailableText;

        var utc = value.Value.ToUniversalTime();
        if (browserTimeZone is null)
            return $"{utc.ToString("ddd, MMM d · h:mm tt", CultureInfo.InvariantCulture)} UTC";

        var local = TimeZoneInfo.ConvertTime(utc, browserTimeZone);
        return $"{local.ToString("ddd, MMM d · h:mm tt", CultureInfo.InvariantCulture)} (UTC{local.ToString("zzz", CultureInfo.InvariantCulture)})";
    }
}

public sealed class PlayerBrowserTimeZoneState
{
    private bool _resolved;

    public TimeZoneInfo? TimeZone { get; private set; }
    public bool IsResolved => _resolved;

    public async Task ResolveAsync(IJSRuntime javascript)
    {
        if (_resolved) return;

        try
        {
            var timeZoneId = await javascript.InvokeAsync<string?>("saturdayScoutTimeZone.getId");
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                try { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
        }
        catch (JSException)
        {
            // Initial/static rendering and disconnected circuits fall back to UTC.
        }
        catch (InvalidOperationException)
        {
            // JavaScript interop may not yet be available during prerendering.
        }

        _resolved = true;
    }
}
