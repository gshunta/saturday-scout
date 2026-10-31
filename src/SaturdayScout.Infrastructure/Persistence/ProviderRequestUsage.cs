namespace SaturdayScout.Infrastructure.Persistence;

public sealed class ProviderRequestUsage
{
    public Guid Id { get; private set; }
    public string Provider { get; private set; } = "";
    public string Endpoint { get; private set; } = "";
    public DateTimeOffset RequestedUtc { get; private set; }
    public string Outcome { get; private set; } = "Reserved";
    public int? HttpStatus { get; private set; }
    public int? RemainingReported { get; private set; }

    private ProviderRequestUsage() { }

    public ProviderRequestUsage(string provider, string endpoint, DateTimeOffset requestedUtc)
    {
        if (requestedUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Request time must be UTC.");
        Id = Guid.NewGuid();
        Provider = provider;
        Endpoint = endpoint;
        RequestedUtc = requestedUtc;
    }

    public void Complete(int? status, int? remaining)
    {
        HttpStatus = status;
        RemainingReported = remaining;
        Outcome = status is >= 200 and < 300 ? "Succeeded" : "Failed";
    }
}
