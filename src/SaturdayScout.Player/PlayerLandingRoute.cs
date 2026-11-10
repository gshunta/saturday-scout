namespace SaturdayScout.Player;

public static class PlayerLandingRoute
{
    public static string Destination(bool authenticated) => authenticated ? "/dashboard" : "/login";
}
