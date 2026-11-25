using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SaturdayScout.Player;

/// <summary>
/// Per-client-IP limits for anonymous Player sign-in. The values bound how many credential
/// submissions one network origin can make; per-account Identity lockout remains the
/// mechanism that protects an individual account from guessing.
/// </summary>
public sealed class PlayerAuthRateLimitOptions
{
    public const string SectionName = "PlayerRateLimiting:Login";
    public const int DefaultPermitLimit = 10;
    public const int DefaultWindowSeconds = 300;
    public const int MaximumPermitLimit = 100;
    public const int MaximumWindowSeconds = 3600;
    public const int SegmentsPerWindow = 5;
    public const int MaximumTrackedClientPartitions = 1024;
    public const int OverflowPartitionCount = 256;

    public int PermitLimit { get; set; } = DefaultPermitLimit;
    public int WindowSeconds { get; set; } = DefaultWindowSeconds;
}

public static class PlayerAuthRateLimiting
{
    public const string LoginPolicy = "player-login";
    internal const string UnknownClientPartition = "unknown";
    public const string RejectionMessage = "Too many sign-in attempts. Wait a few minutes and try again.";

    public static IServiceCollection AddPlayerAuthRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(PlayerAuthRateLimitOptions.SectionName)
            .Get<PlayerAuthRateLimitOptions>() ?? new();
        if (options.PermitLimit is < 1 or > PlayerAuthRateLimitOptions.MaximumPermitLimit)
            throw new InvalidOperationException(
                $"{PlayerAuthRateLimitOptions.SectionName}:PermitLimit must be between 1 and {PlayerAuthRateLimitOptions.MaximumPermitLimit}.");
        if (options.WindowSeconds is < 1 or > PlayerAuthRateLimitOptions.MaximumWindowSeconds)
            throw new InvalidOperationException(
                $"{PlayerAuthRateLimitOptions.SectionName}:WindowSeconds must be between 1 and {PlayerAuthRateLimitOptions.MaximumWindowSeconds}.");

        var window = TimeSpan.FromSeconds(options.WindowSeconds);
        // Keep address-to-partition bookkeeping bounded and expire inactive addresses only
        // after a complete rate-limit window plus a cleanup margin. ASP.NET Core separately
        // evicts idle limiter instances, so the bookkeeping does not retain client IPs forever.
        var partitionRetention = window + TimeSpan.FromMinutes(1);
        services.TryAddSingleton(_ => new PlayerAuthRateLimitPartitionKeys(
            PlayerAuthRateLimitOptions.MaximumTrackedClientPartitions,
            PlayerAuthRateLimitOptions.OverflowPartitionCount,
            partitionRetention,
            TimeProvider.System));
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = WriteRejectionAsync;
            // RemoteIpAddress is the validated client address: Stage 1 forwarded-header
            // processing rewrites it only when the immediate peer is an explicitly trusted
            // proxy. Raw X-Forwarded-For is never read here.
            limiter.AddPolicy(LoginPolicy, context => RateLimitPartition.GetSlidingWindowLimiter(
                context.RequestServices.GetRequiredService<PlayerAuthRateLimitPartitionKeys>()
                    .GetPartitionKey(context.Connection.RemoteIpAddress),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = window,
                    SegmentsPerWindow = PlayerAuthRateLimitOptions.SegmentsPerWindow,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                }));
        });
        return services;
    }

    /// <summary>
    /// IPv4 clients are partitioned by full address. IPv6 clients are partitioned by their
    /// /64 prefix, because one subscriber commonly controls an entire /64 and could otherwise
    /// rotate addresses to obtain unlimited fresh partitions.
    /// </summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null) return UnknownClientPartition;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    // No Retry-After header: the sliding-window limiter does not report a retry time, and a
    // guessed value would be misleading. The message tells the person to wait.
    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        // Player's status-code re-execution must not turn the 429 into the not-found page.
        var statusCodePages = http.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null) statusCodePages.Enabled = false;

        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.ContentType = "text/plain; charset=utf-8";
        await http.Response.WriteAsync(RejectionMessage, cancellationToken);
    }
}

/// <summary>
/// Keeps address bookkeeping bounded, reclaims inactive addresses, and spreads untracked
/// addresses over a fixed number of overflow buckets instead of one globally exhaustible key.
/// </summary>
public sealed class PlayerAuthRateLimitPartitionKeys : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, ClientPartition> clientPartitions = new(StringComparer.Ordinal);
    private readonly int maximumTrackedClients;
    private readonly int overflowPartitionCount;
    private readonly TimeSpan idleRetention;
    private readonly TimeProvider timeProvider;
    private readonly ITimer cleanupTimer;
    private long nextPartition;

    public PlayerAuthRateLimitPartitionKeys(int maximumTrackedClients, int overflowPartitionCount,
        TimeSpan idleRetention, TimeProvider timeProvider)
    {
        if (maximumTrackedClients < 1) throw new ArgumentOutOfRangeException(nameof(maximumTrackedClients));
        if (overflowPartitionCount < 1) throw new ArgumentOutOfRangeException(nameof(overflowPartitionCount));
        if (idleRetention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleRetention));
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.maximumTrackedClients = maximumTrackedClients;
        this.overflowPartitionCount = overflowPartitionCount;
        this.idleRetention = idleRetention;
        this.timeProvider = timeProvider;
        cleanupTimer = timeProvider.CreateTimer(static state =>
            ((PlayerAuthRateLimitPartitionKeys)state!).PruneExpired(), this,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string GetPartitionKey(IPAddress? address)
    {
        var client = PlayerAuthRateLimiting.PartitionKey(address);
        if (client == PlayerAuthRateLimiting.UnknownClientPartition) return client;

        var now = timeProvider.GetTimestamp();
        lock (gate)
        {
            PruneExpired(now);
            if (clientPartitions.TryGetValue(client, out var existing))
            {
                clientPartitions[client] = existing with { LastSeenTimestamp = now };
                return existing.PartitionKey;
            }

            if (clientPartitions.Count >= maximumTrackedClients)
                return OverflowPartition(client);

            var partition = $"client-{++nextPartition}";
            clientPartitions.Add(client, new ClientPartition(partition, now));
            return partition;
        }
    }

    private string OverflowPartition(string client)
    {
        // StringComparer uses a process-randomized hash. This limits systematic selection of
        // a particular overflow bucket while keeping the overflow key set strictly bounded.
        var bucket = (uint)StringComparer.Ordinal.GetHashCode(client) % (uint)overflowPartitionCount;
        return $"overflow-{bucket}";
    }

    private void PruneExpired()
    {
        lock (gate) PruneExpired(timeProvider.GetTimestamp());
    }

    private void PruneExpired(long now)
    {
        foreach (var inactive in clientPartitions
            .Where(entry => timeProvider.GetElapsedTime(entry.Value.LastSeenTimestamp, now) >= idleRetention)
            .Select(entry => entry.Key)
            .ToArray())
            clientPartitions.Remove(inactive);
    }

    public void Dispose() => cleanupTimer.Dispose();

    private sealed record ClientPartition(string PartitionKey, long LastSeenTimestamp);
}
