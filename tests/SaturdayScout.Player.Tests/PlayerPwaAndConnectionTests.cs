using System.Buffers.Binary;
using System.Text.Json;
using System.Xml.Linq;
using SaturdayScout.Application;
using SaturdayScout.Player;
using Xunit;

namespace SaturdayScout.Player.Tests;

public sealed class PlayerPwaAndConnectionTests
{
    private static string Asset(string path) => Path.Combine(AppContext.BaseDirectory, "PwaAssets", path);
    private static string Source(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, path));
    }

    [Fact]
    public void PwaStartUrlTargetsTheExistingRootRouteAndLoginRedirectsAuthenticatedPlayersHome()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Asset("manifest.v2.webmanifest")));
        Assert.Equal("/", manifest.RootElement.GetProperty("start_url").GetString());
        Assert.Equal("/", manifest.RootElement.GetProperty("scope").GetString());

        var home = Source("src/SaturdayScout.Player/Components/Pages/Home.razor");
        var landing = Source("src/SaturdayScout.Player/Components/Pages/Landing.razor");
        var login = Source("src/SaturdayScout.Player/Components/Pages/Login.razor");
        var routes = Source("src/SaturdayScout.Player/Components/Routes.razor");
        var app = Source("src/SaturdayScout.Player/Components/App.razor");
        Assert.Contains("@page \"/\"", landing);
        Assert.Contains("[AllowAnonymous]", landing);
        Assert.Contains("@page \"/dashboard\"", home);
        Assert.Contains("[Authorize(Policy = \"Player\")]", home);
        Assert.Contains("PlayerLandingRoute.Destination(authentication.User.Identity?.IsAuthenticated == true)", landing);
        Assert.Equal("/login", PlayerLandingRoute.Destination(authenticated: false));
        Assert.Equal("/dashboard", PlayerLandingRoute.Destination(authenticated: true));
        Assert.Contains("if (authentication.User.Identity?.IsAuthenticated == true)", login);
        Assert.Contains("Navigation.NavigateTo(\"/\", replace: true)", login);
        Assert.Contains("<RedirectToLogin />", routes);
        Assert.Contains("rel=\"apple-touch-icon\" sizes=\"180x180\"", app);
        Assert.Contains("icons/apple-touch-icon.png", app);
    }

    [Fact]
    public void ManifestHasInstallMetadataAndRealSizedIcons()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Asset("manifest.v2.webmanifest")));
        var root = manifest.RootElement;

        Assert.Equal("Saturday Scout", root.GetProperty("name").GetString());
        Assert.Equal("Scout", root.GetProperty("short_name").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        Assert.Equal("#101827", root.GetProperty("theme_color").GetString());
        Assert.Equal("#101827", root.GetProperty("background_color").GetString());

        var icons = root.GetProperty("icons").EnumerateArray().ToArray();
        Assert.Contains(icons, icon => icon.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, icon => icon.GetProperty("sizes").GetString() == "512x512");
        foreach (var icon in icons)
        {
            var path = icon.GetProperty("src").GetString()!;
            Assert.True(File.Exists(Asset(path.TrimStart('/'))));
            if (icon.GetProperty("type").GetString() == "image/svg+xml")
            {
                var svg = XDocument.Load(Asset(path.TrimStart('/'))).Root!;
                Assert.Equal("svg", svg.Name.LocalName);
                Assert.Equal(icon.GetProperty("sizes").GetString()!.Split('x')[0], svg.Attribute("width")?.Value);
            }
        }

        var appleIcon = File.ReadAllBytes(Asset("icons/apple-touch-icon.png"));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, appleIcon[..8]);
        Assert.Equal(180, BinaryPrimitives.ReadInt32BigEndian(appleIcon.AsSpan(16, 4)));
        Assert.Equal(180, BinaryPrimitives.ReadInt32BigEndian(appleIcon.AsSpan(20, 4)));
        Assert.Contains(icons, icon => icon.GetProperty("sizes").GetString() == "180x180" &&
            icon.GetProperty("type").GetString() == "image/png");
    }

    [Fact]
    public void ServiceWorkerCachesOnlyVersionedPublicStaticFiles()
    {
        var serviceWorker = File.ReadAllText(Asset("service-worker.v1.js"));
        var allowlist = serviceWorker.Split("const PUBLIC_ASSETS = [", StringSplitOptions.None)[1]
            .Split("];", StringSplitOptions.None)[0];
        var cachedPaths = System.Text.RegularExpressions.Regex.Matches(allowlist, "\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();

        Assert.Equal(new[]
        {
            "/manifest.v2.webmanifest",
            "/icons/scout-192.v1.svg",
            "/icons/scout-512.v1.svg",
            "/icons/apple-touch-icon.png"
        }, cachedPaths);
        Assert.Contains("request.method !== \"GET\"", serviceWorker);
        Assert.Contains("url.origin !== self.location.origin", serviceWorker);
        Assert.Contains("!PUBLIC_ASSETS.includes(url.pathname)", serviceWorker);
        Assert.Contains("saturday-scout-public-static-v2", serviceWorker);
        Assert.DoesNotContain("/api/", serviceWorker);
        Assert.DoesNotContain("/auth/", serviceWorker);
        Assert.DoesNotContain("_blazor", serviceWorker);
    }

    [Fact]
    public void MobileStylesProvideSafeAreaTouchTargetsAndDarkPalette()
    {
        var css = File.ReadAllText(Asset("app.css"));
        Assert.Contains("#101827", css);
        Assert.Contains("#222b38", css);
        Assert.Contains("#34445b", css);
        Assert.Contains("#f5f7fa", css);
        Assert.Contains("#39b8a0", css);
        Assert.Contains("env(safe-area-inset-bottom)", css);
        Assert.Contains("min-height: 48px", css);
        Assert.Contains("@media (prefers-reduced-motion", css);
        Assert.Contains("@media (max-width: 420px)", css);
        Assert.Contains("@media (display-mode: standalone) and (max-width: 719px)", css);
        Assert.Contains("env(safe-area-inset-bottom, 0px)", css);
    }

    [Fact]
    public void SharedPlayerNavigationIsAuthorizedAccessibleAndOutsideFilteredHeader()
    {
        var layout = Source("src/SaturdayScout.Player/Components/Layout/MainLayout.razor");
        var css = File.ReadAllText(Asset("app.css"));
        var headerEnd = layout.IndexOf("</header>", StringComparison.Ordinal);
        var navStart = layout.IndexOf("<nav class=\"player-nav\"", StringComparison.Ordinal);

        Assert.True(headerEnd >= 0 && navStart > headerEnd);
        Assert.Contains("<AuthorizeView Policy=\"Player\">", layout);
        Assert.Contains("aria-label=\"Player navigation\"", layout);
        Assert.Contains("<NavLink href=", layout); // NavLink provides active styling and aria-current.
        Assert.Contains("href=\"/picks\"", layout);
        Assert.Contains("href=\"/standings\"", layout);
        Assert.Contains("href=\"/league\"", layout);
        Assert.Contains("href=\"/profile\"", layout);
        Assert.Contains("href=\"/\"", layout); // Brand link routes authenticated players through the dashboard landing gate.
        Assert.Contains(".player-nav a:focus-visible", css);
        Assert.Contains(".player-nav a.active", css);
        Assert.Contains("min-height: 3.4rem", css);
        Assert.Contains("inset-block-end: 0", css);

        var routes = Source("src/SaturdayScout.Player/Components/Routes.razor");
        Assert.Contains("DefaultLayout=\"typeof(Layout.MainLayout)\"", routes);
        foreach (var path in new[] { "Home", "Picks", "Standings", "League", "Profile" })
        {
            var page = Source($"src/SaturdayScout.Player/Components/Pages/{path}.razor");
            Assert.DoesNotContain("@layout", page);
        }

        var serviceWorker = File.ReadAllText(Asset("service-worker.v1.js"));
        Assert.DoesNotContain("/app.css", serviceWorker);
        Assert.DoesNotContain("request.mode === \"navigate\"", serviceWorker);
    }

    [Theory]
    [InlineData("2026-11-08T06:30:00+00:00", "Nov 7 · 10:30 PM (UTC-08:00)")]
    [InlineData("2026-03-08T10:30:00+00:00", "Mar 8 · 3:30 AM (UTC-07:00)")]
    public void PlayerDatesUseBrowserZoneAndCorrectLocalCalendarDay(string utcText, string expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var formatted = PlayerLocalTimeFormatter.Format(DateTimeOffset.Parse(utcText), zone, "Unavailable");

        Assert.Contains(expected, formatted);
    }

    [Fact]
    public void PlayerDatesFallBackToUtcUntilBrowserZoneIsAvailable()
    {
        var formatted = PlayerLocalTimeFormatter.Format(
            DateTimeOffset.Parse("2026-11-08T06:30:00+00:00"), null, "Unavailable");

        Assert.Equal("Sun, Nov 8 · 6:30 AM UTC", formatted);
        Assert.Equal("Unavailable", PlayerLocalTimeFormatter.Format(null, null, "Unavailable"));
    }

    [Fact]
    public void StandingsHeadingAndMobileRowsKeepAvailableWidthAtRequestedWidths()
    {
        var css = File.ReadAllText(Asset("app.css"));
        var standings = Source("src/SaturdayScout.Player/Components/Pages/Standings.razor");

        Assert.Contains(".sr-only", css);
        Assert.Contains("<caption class=\"sr-only\"", standings);
        Assert.Contains(".standings-page > h1", css);
        Assert.Contains("width: 100%; min-width: 0", css);
        Assert.Contains(".standings-table tbody tr { display: grid; width: 100%; min-width: 0;", css);
        Assert.Contains("@media (max-width: 600px)", css);
        Assert.Contains("@media (max-width: 420px)", css);
        Assert.Contains("@media (min-width: 720px)", css);
    }

    [Fact]
    public void PicksUseCompactVisibleMatchupAndKeepAccessibleWinnerInstruction()
    {
        var picks = Source("src/SaturdayScout.Player/Components/Pages/Picks.razor");

        Assert.Contains("class=\"matchup-label\"", picks);
        Assert.Contains("Select a winner for @selection.Game.AwayTeam at @selection.Game.HomeTeam", picks);
        Assert.DoesNotContain("Pick winner for @selection.Game.AwayTeam", picks);
    }

    [Fact]
    public void ConnectionStateNotifiesOnlyWhenConnectivityChanges()
    {
        var state = new PlayerConnectionState();
        var notifications = 0;
        state.Changed += () => notifications++;

        state.SetConnected(false);
        state.SetConnected(false);
        state.SetConnected(true);

        Assert.True(state.IsConnected);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void ReconnectPreservesLocalChangesAndConflictsOnNewerServerSnapshot()
    {
        var pick = new PickDto(Guid.NewGuid(), Guid.NewGuid(), 1);
        var current = new PickSheetDto(Guid.NewGuid(), Guid.NewGuid(), "Draft", null, 3, [pick]);
        var same = current with { Picks = [pick] };
        var newer = current with { Version = 4, Picks = [pick with { ConfidenceRank = 2 }] };

        Assert.Equal(PlayerDraftReconnectDecision.KeepLocal,
            PlayerPickDraftRules.DecideReconnect(current, same, hasUnsavedChanges: true));
        Assert.Equal(PlayerDraftReconnectDecision.AdoptServer,
            PlayerPickDraftRules.DecideReconnect(current, same, hasUnsavedChanges: false));
        Assert.Equal(PlayerDraftReconnectDecision.Conflict,
            PlayerPickDraftRules.DecideReconnect(current, newer, hasUnsavedChanges: false));
        Assert.Equal(PlayerDraftReconnectDecision.Conflict,
            PlayerPickDraftRules.DecideReconnect(null, current, hasUnsavedChanges: true));
    }
}
