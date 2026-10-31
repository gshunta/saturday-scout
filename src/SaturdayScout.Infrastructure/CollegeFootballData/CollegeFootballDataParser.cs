using System.Text.Json;
using SaturdayScout.Application;
using SaturdayScout.Domain;

namespace SaturdayScout.Infrastructure.CollegeFootballData;

public static class CollegeFootballDataParser
{
    public const string ProviderName = "CollegeFootballData";

    public static ImportedWeek Parse(string teamsJson, string scheduleJson, string resultsJson, DateTimeOffset retrievedUtc)
    {
        if (retrievedUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Retrieval time must be UTC.");
        using var teamsDoc = JsonDocument.Parse(teamsJson);
        using var scheduleDoc = JsonDocument.Parse(scheduleJson);
        using var resultsDoc = JsonDocument.Parse(resultsJson);
        var issues = new List<ImportIssue>();
        var teams = new Dictionary<string, ImportedTeam>();
        foreach (var row in teamsDoc.RootElement.EnumerateArray())
        {
            var id = RequiredInt(row, "id").ToString();
            var name = RequiredString(row, "school");
            var abbreviation = OptionalString(row, "abbreviation");
            if (string.IsNullOrWhiteSpace(abbreviation))
            {
                abbreviation = $"CFBD-{id}";
                issues.Add(new ImportIssue(id, "Missing abbreviation; stable provider-ID fallback used."));
            }
            teams[id] = new ImportedTeam(ProviderName, id, name, abbreviation);
        }

        var results = resultsDoc.RootElement.EnumerateArray()
            .ToDictionary(row => RequiredInt(row, "id").ToString(), row => row.Clone());
        var games = new List<ImportedGame>();
        foreach (var row in scheduleDoc.RootElement.GetProperty("games").EnumerateArray())
        {
            var id = RequiredInt(row, "id").ToString();
            var kickoff = OptionalDate(row, "startDate");
            if (kickoff is null)
            {
                issues.Add(new ImportIssue(id, "Missing or unusable kickoff; game skipped."));
                continue;
            }
            var home = row.GetProperty("homeTeam");
            var away = row.GetProperty("awayTeam");
            var homeId = RequiredInt(home, "id").ToString();
            var awayId = RequiredInt(away, "id").ToString();
            AddScheduleTeam(home, homeId);
            AddScheduleTeam(away, awayId);
            var rawStatus = OptionalString(row, "status");
            if (string.IsNullOrWhiteSpace(rawStatus))
            {
                issues.Add(new ImportIssue(id, "Missing status; game skipped."));
                continue;
            }
            GameStatus? status = rawStatus switch
            {
                "scheduled" => GameStatus.Scheduled,
                "in_progress" => GameStatus.InProgress,
                "completed" => GameStatus.Final,
                _ => null
            };
            if (status is null)
            {
                issues.Add(new ImportIssue(id, $"Unsupported status '{rawStatus}'; game skipped."));
                continue;
            }
            int? homeScore = null;
            int? awayScore = null;
            if (status == GameStatus.Final)
            {
                if (!results.TryGetValue(id, out var result) ||
                    !result.TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.True ||
                    !TryNonnegativeInt(result, "homePoints", out homeScore) ||
                    !TryNonnegativeInt(result, "awayPoints", out awayScore))
                {
                    issues.Add(new ImportIssue(id, "Completed status lacks corroborated nonnegative final scores; game skipped."));
                    continue;
                }
            }
            else if (results.TryGetValue(id, out var result) &&
                result.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True)
            {
                issues.Add(new ImportIssue(id, "Schedule and results completion disagree; non-final status retained."));
            }
            games.Add(new ImportedGame(ProviderName, id, Sport.CollegeFootball,
                RequiredInt(row, "season"), RequiredInt(row, "week"), homeId, awayId,
                kickoff, status, rawStatus, homeScore, awayScore, retrievedUtc));
        }
        return new ImportedWeek(teams.Values.ToArray(), games, issues);

        void AddScheduleTeam(JsonElement row, string id)
        {
            if (teams.ContainsKey(id)) return;
            var name = RequiredString(row, "name");
            teams[id] = new ImportedTeam(ProviderName, id, name, $"CFBD-{id}");
            issues.Add(new ImportIssue(id, "Team absent from team feed; schedule name and provider-ID abbreviation used."));
        }
    }

    private static int RequiredInt(JsonElement row, string name) => row.GetProperty(name).GetInt32();
    private static string RequiredString(JsonElement row, string name) => row.GetProperty(name).GetString()
        ?? throw new FormatException($"Missing {name}.");
    private static string? OptionalString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? OptionalDate(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetString(), out var parsed)) return null;
        return parsed.ToUniversalTime();
    }
    private static bool TryNonnegativeInt(JsonElement row, string name, out int? value)
    {
        value = null;
        if (!row.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var score) || score < 0) return false;
        value = score;
        return true;
    }
}
