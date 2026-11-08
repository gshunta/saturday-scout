using SaturdayScout.Application;

namespace SaturdayScout.Player;

public static class PlayerPickStatus
{
    public static string ToLabel(PlayerSlateDto slate, PickSheetDto? sheet, DateTimeOffset nowUtc)
    {
        if (slate.Status == "Finalized") return "Finalized";
        if (sheet?.Status == "Submitted") return "Submitted";
        if (slate.Status == "Locked" || slate.LockUtc is { } lockUtc && nowUtc >= lockUtc) return "Locked";
        return sheet is null ? "Not Started" : "Draft";
    }
}
