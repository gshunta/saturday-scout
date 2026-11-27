using System.Net;
using System.Net.Sockets;

namespace SaturdayScout.Admin;

/// <summary>Bounds and expires the client-address keys used by Admin authentication limiters.</summary>
public sealed class AdminAuthRateLimitPartitions : IDisposable
{
    public const int MaximumTrackedClients = 1024;
    public const int OverflowPartitionCount = 256;
    private static readonly TimeSpan IdleRetention = TimeSpan.FromMinutes(2);

    private readonly object gate = new();
    private readonly Dictionary<string, TrackedClient> clients = new(StringComparer.Ordinal);
    private readonly Queue<int> availableSlots = new(Enumerable.Range(1, MaximumTrackedClients));
    private readonly TimeProvider clock;
    private readonly ITimer cleanupTimer;

    public AdminAuthRateLimitPartitions() : this(TimeProvider.System) { }

    public AdminAuthRateLimitPartitions(TimeProvider clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        cleanupTimer = clock.CreateTimer(static state => ((AdminAuthRateLimitPartitions)state!).PruneExpired(),
            this, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string GetPartitionKey(string policy, IPAddress? address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        var client = NormalizeAddress(address);
        if (client is null) return $"{policy}:unknown";

        var now = clock.GetTimestamp();
        lock (gate)
        {
            PruneExpired(now);
            if (!clients.TryGetValue(client, out var tracked))
            {
                if (availableSlots.Count == 0)
                {
                    var bucket = (uint)StringComparer.Ordinal.GetHashCode($"{policy}:{client}") % OverflowPartitionCount;
                    return $"{policy}:overflow-{bucket}";
                }

                tracked = new TrackedClient(availableSlots.Dequeue(), now);
            }

            clients[client] = tracked with { LastSeenTimestamp = now };
            return $"{policy}:client-{tracked.Slot}";
        }
    }

    private static string? NormalizeAddress(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private void PruneExpired() { lock (gate) PruneExpired(clock.GetTimestamp()); }

    private void PruneExpired(long now)
    {
        foreach (var expired in clients
                     .Where(pair => clock.GetElapsedTime(pair.Value.LastSeenTimestamp, now) >= IdleRetention)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            var tracked = clients[expired];
            clients.Remove(expired);
            availableSlots.Enqueue(tracked.Slot);
        }
    }

    public void Dispose() => cleanupTimer.Dispose();

    private sealed record TrackedClient(int Slot, long LastSeenTimestamp);
}
