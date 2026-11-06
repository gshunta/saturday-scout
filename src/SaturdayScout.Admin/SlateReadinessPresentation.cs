namespace SaturdayScout.Admin;

public static class SlateReadinessPresentation
{
    public static string Label(string status, bool readyForPublishing) => status switch
    {
        "Draft" => readyForPublishing ? "Ready to publish" : "Incomplete draft",
        "Published" => "Published",
        "Locked" or "Finalized" => "Closed",
        _ => "—"
    };
}
