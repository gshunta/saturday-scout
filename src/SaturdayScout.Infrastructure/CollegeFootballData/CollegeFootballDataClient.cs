using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using SaturdayScout.Application;
using SaturdayScout.Domain;
using SaturdayScout.Infrastructure.Persistence;

namespace SaturdayScout.Infrastructure.CollegeFootballData;

public sealed record CollegeFootballDataOptions(string ApiKey, int ScoutMonthlyCap, int SharedRemaining);

public sealed class CfbdRequestBudget(SaturdayScoutDbContext db, CollegeFootballDataOptions options)
{
    private int _attemptsThisRun;

    public async Task<ProviderRequestUsage> ReserveAsync(string endpoint, CancellationToken ct)
    {
        if (options.ScoutMonthlyCap <= 0 || options.SharedRemaining <= 0)
            throw new InvalidOperationException("Set positive CFBD_SCOUT_MONTHLY_CAP and CFBD_SHARED_REMAINING values.");
        if (_attemptsThisRun >= options.SharedRemaining)
            throw new InvalidOperationException("The operator-provided shared remaining-call allowance is exhausted.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(789352105)", ct);
        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var ownCalls = await db.ProviderRequestUsages.CountAsync(x =>
            x.Provider == CollegeFootballDataParser.ProviderName && x.RequestedUtc >= monthStart, ct);
        if (ownCalls >= options.ScoutMonthlyCap)
            throw new InvalidOperationException("Saturday Scout's configured monthly call cap is exhausted.");
        var lastReported = await db.ProviderRequestUsages
            .Where(x => x.Provider == CollegeFootballDataParser.ProviderName &&
                x.RequestedUtc >= monthStart && x.RemainingReported != null)
            .OrderByDescending(x => x.RequestedUtc).FirstOrDefaultAsync(ct);
        if (lastReported is not null)
        {
            var reservedSince = await db.ProviderRequestUsages.CountAsync(x =>
                x.Provider == CollegeFootballDataParser.ProviderName && x.RequestedUtc > lastReported.RequestedUtc, ct);
            if (lastReported.RemainingReported <= reservedSince)
                throw new InvalidOperationException("The provider's last reported shared-call remainder is exhausted.");
        }
        var usage = new ProviderRequestUsage(CollegeFootballDataParser.ProviderName, endpoint, now);
        db.ProviderRequestUsages.Add(usage);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _attemptsThisRun++;
        return usage;
    }

    public async Task CompleteAsync(ProviderRequestUsage usage, int? status, int? remaining, CancellationToken ct)
    {
        usage.Complete(status, remaining);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class CollegeFootballDataClient(HttpClient http, CfbdRequestBudget budget,
    CollegeFootballDataOptions options) : IFootballWeekProvider
{
    public async Task<ImportedWeek> GetWeekAsync(Sport sport, int season, int week, CancellationToken ct)
    {
        if (sport != Sport.CollegeFootball) throw new ArgumentException("CFBD supports college football only.");
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("CFBD_API_KEY is required for live requests.");
        var teams = await GetAsync($"/teams?year={season}", ct);
        var schedule = await GetAsync($"/games/schedule?year={season}&seasonType=regular&week={week}", ct);
        var results = await GetAsync($"/games?year={season}&seasonType=regular&week={week}", ct);
        return CollegeFootballDataParser.Parse(teams, schedule, results, DateTimeOffset.UtcNow);
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var usage = await budget.ReserveAsync(path.Split('?')[0], ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                int? remaining = null;
                if (response.Headers.TryGetValues("X-CallLimit-Remaining", out var values) &&
                    int.TryParse(values.FirstOrDefault(), out var parsed))
                    remaining = parsed;
                var body = await response.Content.ReadAsStringAsync(ct);
                await budget.CompleteAsync(usage, (int)response.StatusCode, remaining, ct);
                if (response.IsSuccessStatusCode) return body;
                if (attempt == 3 || response.StatusCode is not (HttpStatusCode.TooManyRequests or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or
                    HttpStatusCode.InternalServerError))
                    throw new HttpRequestException($"CFBD {path.Split('?')[0]} returned HTTP {(int)response.StatusCode}.",
                        null, response.StatusCode);
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(500 * attempt);
                await Task.Delay(delay > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : delay, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (usage.Outcome == "Reserved") await budget.CompleteAsync(usage, null, null, ct);
                if (attempt == 3 || ex is HttpRequestException { StatusCode: not null })
                    throw;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }
        throw new InvalidOperationException("CFBD retries exhausted.");
    }
}
