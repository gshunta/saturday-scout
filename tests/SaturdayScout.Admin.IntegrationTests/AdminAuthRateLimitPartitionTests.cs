using System.Net;
using SaturdayScout.Admin;
using Xunit;

namespace SaturdayScout.Admin.IntegrationTests;

public sealed class AdminAuthRateLimitPartitionTests
{
    [Fact]
    public void AddressBookIsBoundedAndOverflowIsDistributed()
    {
        using var partitions = new AdminAuthRateLimitPartitions(new ManualTimeProvider());
        var keys = Enumerable.Range(1, AdminAuthRateLimitPartitions.MaximumTrackedClients)
            .Select(index => partitions.GetPartitionKey("login", IPAddress.Parse($"10.{index / 65536}.{index / 256 % 256}.{index % 256}")))
            .ToArray();

        Assert.All(keys, key => Assert.StartsWith("login:client-", key, StringComparison.Ordinal));
        var overflow = partitions.GetPartitionKey("login", IPAddress.Parse("192.0.2.1"));
        Assert.StartsWith("login:overflow-", overflow, StringComparison.Ordinal);
        var overflowBuckets = Enumerable.Range(1, 64)
            .Select(last => partitions.GetPartitionKey("login", IPAddress.Parse($"198.18.0.{last}")))
            .Distinct(StringComparer.Ordinal).Count();
        Assert.InRange(overflowBuckets, 2, AdminAuthRateLimitPartitions.OverflowPartitionCount);
    }

    [Fact]
    public void ExpiredClientAddressIsReclaimedAfterRetentionWindow()
    {
        var clock = new ManualTimeProvider();
        using var partitions = new AdminAuthRateLimitPartitions(clock);
        var address = IPAddress.Parse("192.0.2.44");
        var first = partitions.GetPartitionKey("login", address);

        clock.Advance(TimeSpan.FromMinutes(2));
        var afterExpiry = partitions.GetPartitionKey("login", IPAddress.Parse("198.51.100.10"));
        var reentered = partitions.GetPartitionKey("login", address);

        Assert.StartsWith("login:client-", afterExpiry, StringComparison.Ordinal);
        Assert.StartsWith("login:client-", reentered, StringComparison.Ordinal);
        Assert.NotEqual(first, reentered);
    }

    [Fact]
    public void RepeatedExpirationCyclesReuseBoundedLimiterKeys()
    {
        var clock = new ManualTimeProvider();
        using var partitions = new AdminAuthRateLimitPartitions(clock);
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var cycle = 0; cycle < 4; cycle++)
        {
            for (var index = 0; index < AdminAuthRateLimitPartitions.MaximumTrackedClients; index++)
            {
                var address = IPAddress.Parse($"10.{cycle + 1}.{index / 256}.{index % 256}");
                observedKeys.Add(partitions.GetPartitionKey("login", address));
            }

            if (cycle < 3) clock.Advance(TimeSpan.FromMinutes(2));
        }

        Assert.Equal(AdminAuthRateLimitPartitions.MaximumTrackedClients, observedKeys.Count);
    }

    [Fact]
    public void ConcurrentAddressArrivalsStayWithinTheConfiguredPartitionBound()
    {
        using var partitions = new AdminAuthRateLimitPartitions(new ManualTimeProvider());
        var keys = new string[AdminAuthRateLimitPartitions.MaximumTrackedClients * 4];
        Parallel.For(0, keys.Length, index =>
        {
            var address = IPAddress.Parse($"10.{index / 65536}.{index / 256 % 256}.{index % 256}");
            keys[index] = partitions.GetPartitionKey("mfa", address);
        });

        Assert.All(keys, key => Assert.StartsWith("mfa:", key, StringComparison.Ordinal));
        Assert.Contains(keys, key => key.StartsWith("mfa:overflow-", StringComparison.Ordinal));
    }

    [Fact]
    public void Ipv4MappedAndIpv6AddressesUseNormalizedPartitions()
    {
        using var partitions = new AdminAuthRateLimitPartitions(new ManualTimeProvider());
        Assert.Equal(partitions.GetPartitionKey("login", IPAddress.Parse("192.0.2.7")),
            partitions.GetPartitionKey("login", IPAddress.Parse("::ffff:192.0.2.7")));
        Assert.Equal(partitions.GetPartitionKey("mfa", IPAddress.Parse("2001:db8:1:2::1")),
            partitions.GetPartitionKey("mfa", IPAddress.Parse("2001:db8:1:2:ffff::9")));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
