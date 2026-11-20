using System.Threading;

namespace SaturdayScout.Player;

public enum DevelopmentWriteOperation
{
    SaveDraft,
    SubmitPicks
}

/// <summary>A startup-armed, single-use Development fault bound to one exact QA player, league, and slate.</summary>
public sealed class DevelopmentAmbiguousWriteFault
{
    public static readonly Guid DefaultQaLeagueId = Guid.Parse("c350c497-5043-45cf-8ce6-a8bdcc4d6775");
    public static readonly Guid DefaultQaSlateId = Guid.Parse("130f3cfd-de08-1d24-8957-4bd3e4837dd0");
    public static readonly Guid DefaultQaPlayerId = Guid.Parse("0cc5eaa0-cb07-4017-9992-3e66d3f81b71");
    public static readonly Guid QaLeagueId = DefaultQaLeagueId;
    public static readonly Guid QaSlateId = DefaultQaSlateId;
    public static readonly Guid QaPlayerId = DefaultQaPlayerId;

    private const int Armed = 0;
    private const int InFlight = 1;
    private const int Consumed = 2;
    private readonly DevelopmentWriteOperation? operation;
    private int state;

    private DevelopmentAmbiguousWriteFault(DevelopmentWriteOperation? operation,
        Guid playerId, Guid leagueId, Guid slateId)
    {
        this.operation = operation;
        TargetPlayerId = playerId;
        TargetLeagueId = leagueId;
        TargetSlateId = slateId;
        state = operation is null ? Consumed : Armed;
    }

    public bool IsArmed => Volatile.Read(ref state) == Armed;
    public Guid TargetPlayerId { get; }
    public Guid TargetLeagueId { get; }
    public Guid TargetSlateId { get; }

    public static DevelopmentAmbiguousWriteFault FromConfiguration(bool isDevelopment,
        string? operationSetting, string? approvalSetting,
        string? playerIdSetting = null, string? leagueIdSetting = null, string? slateIdSetting = null)
    {
        var hasOperation = !string.IsNullOrWhiteSpace(operationSetting);
        var hasApproval = !string.IsNullOrWhiteSpace(approvalSetting);
        var hasTargetSetting = !string.IsNullOrWhiteSpace(playerIdSetting) ||
                               !string.IsNullOrWhiteSpace(leagueIdSetting) ||
                               !string.IsNullOrWhiteSpace(slateIdSetting);
        if (!isDevelopment)
        {
            if (hasOperation || hasApproval || hasTargetSetting)
                throw new InvalidOperationException("Development ambiguous-write simulation cannot be configured outside Development.");
            return Create(null, null, null, null);
        }

        if (!hasOperation && !hasApproval)
        {
            if (hasTargetSetting)
                throw new InvalidOperationException("Ambiguous-write target IDs require an armed Development operation and explicit approval.");
            return Create(null, null, null, null);
        }
        if (!hasOperation || !string.Equals(approvalSetting, "yes", StringComparison.Ordinal))
            throw new InvalidOperationException("Arming the Development ambiguous-write simulation requires explicit approval.");

        var parsedOperation = operationSetting switch
        {
            "save-draft" => DevelopmentWriteOperation.SaveDraft,
            "submit-picks" => DevelopmentWriteOperation.SubmitPicks,
            _ => throw new InvalidOperationException("Development ambiguous-write operation must be save-draft or submit-picks.")
        };

        if (hasTargetSetting && (string.IsNullOrWhiteSpace(playerIdSetting) ||
                                 string.IsNullOrWhiteSpace(leagueIdSetting) ||
                                 string.IsNullOrWhiteSpace(slateIdSetting)))
            throw new InvalidOperationException("All three Development ambiguous-write target IDs must be supplied together.");

        return Create(parsedOperation, playerIdSetting, leagueIdSetting, slateIdSetting);
    }

    private static DevelopmentAmbiguousWriteFault Create(DevelopmentWriteOperation? operation,
        string? playerIdSetting, string? leagueIdSetting, string? slateIdSetting)
    {
        var playerId = ParseTargetId(playerIdSetting, DefaultQaPlayerId, "player");
        var leagueId = ParseTargetId(leagueIdSetting, DefaultQaLeagueId, "league");
        var slateId = ParseTargetId(slateIdSetting, DefaultQaSlateId, "slate");
        return new DevelopmentAmbiguousWriteFault(operation, playerId, leagueId, slateId);
    }

    private static Guid ParseTargetId(string? value, Guid defaultValue, string target)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
            throw new InvalidOperationException($"Development ambiguous-write {target} target ID must be a non-empty GUID.");
        return parsed;
    }

    public Lease? TryBegin(DevelopmentWriteOperation requestedOperation, Guid playerId, Guid slateId)
    {
        if (requestedOperation != operation || playerId != TargetPlayerId || slateId != TargetSlateId ||
            Interlocked.CompareExchange(ref state, InFlight, Armed) != Armed)
            return null;
        return new Lease(this);
    }

    private bool Consume() => Interlocked.CompareExchange(ref state, Consumed, InFlight) == InFlight;
    private void Release() => Interlocked.CompareExchange(ref state, Armed, InFlight);

    public sealed class Lease : IDisposable
    {
        private readonly DevelopmentAmbiguousWriteFault owner;
        private int completed;

        internal Lease(DevelopmentAmbiguousWriteFault owner) => this.owner = owner;

        public bool Consume()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0) return false;
            return owner.Consume();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref completed, 1) == 0) owner.Release();
        }
    }
}
