using SaturdayScout.Player;
using System.Collections.Concurrent;
using Xunit;

namespace SaturdayScout.Player.Tests;

public sealed class DevelopmentAmbiguousWriteFaultTests
{
    [Fact]
    public void IsDisabledWithoutExplicitDevelopmentApproval()
    {
        var fault = DevelopmentAmbiguousWriteFault.FromConfiguration(true, null, null);
        Assert.False(fault.IsArmed);
        Assert.Null(fault.TryBegin(DevelopmentWriteOperation.SaveDraft,
            DevelopmentAmbiguousWriteFault.QaPlayerId, DevelopmentAmbiguousWriteFault.QaSlateId));
    }

    [Theory]
    [InlineData("save-draft")]
    [InlineData("submit-picks")]
    public void RequiresApprovalAndIsRejectedOutsideDevelopment(string operation)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentAmbiguousWriteFault.FromConfiguration(true, operation, null));
        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentAmbiguousWriteFault.FromConfiguration(false, operation, "yes"));
        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentAmbiguousWriteFault.FromConfiguration(false, null, "yes"));
    }

    [Fact]
    public void CustomTargetsRequireAllExactIdsAndAreDevelopmentApprovalBound()
    {
        var playerId = Guid.NewGuid();
        var leagueId = Guid.NewGuid();
        var slateId = Guid.NewGuid();
        var fault = DevelopmentAmbiguousWriteFault.FromConfiguration(true, "submit-picks", "yes",
            playerId.ToString("D"), leagueId.ToString("D"), slateId.ToString("D"));

        Assert.Null(fault.TryBegin(DevelopmentWriteOperation.SubmitPicks, playerId, Guid.NewGuid()));
        Assert.Null(fault.TryBegin(DevelopmentWriteOperation.SubmitPicks, Guid.NewGuid(), slateId));
        using var lease = fault.TryBegin(DevelopmentWriteOperation.SubmitPicks, playerId, slateId);
        Assert.NotNull(lease);

        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentAmbiguousWriteFault.FromConfiguration(true, "submit-picks", "yes",
                playerId.ToString("D"), leagueId.ToString("D"), null));
        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentAmbiguousWriteFault.FromConfiguration(true, "submit-picks", "yes",
                "not-a-guid", leagueId.ToString("D"), slateId.ToString("D")));
        Assert.Throws<InvalidOperationException>(() =>
            DevelopmentAmbiguousWriteFault.FromConfiguration(false, "submit-picks", "yes",
                playerId.ToString("D"), leagueId.ToString("D"), slateId.ToString("D")));
    }

    [Fact]
    public void OnlyTheArmedOperationAndExactPlayerAndSlateCanAcquireTheOneShot()
    {
        var fault = DevelopmentAmbiguousWriteFault.FromConfiguration(true, "save-draft", "yes");

        Assert.Null(fault.TryBegin(DevelopmentWriteOperation.SubmitPicks,
            DevelopmentAmbiguousWriteFault.QaPlayerId, DevelopmentAmbiguousWriteFault.QaSlateId));
        Assert.Null(fault.TryBegin(DevelopmentWriteOperation.SaveDraft, Guid.NewGuid(),
            DevelopmentAmbiguousWriteFault.QaSlateId));
        Assert.Null(fault.TryBegin(DevelopmentWriteOperation.SaveDraft,
            DevelopmentAmbiguousWriteFault.QaPlayerId, Guid.NewGuid()));
        Assert.True(fault.IsArmed);

        using (var lease = fault.TryBegin(DevelopmentWriteOperation.SaveDraft,
                   DevelopmentAmbiguousWriteFault.QaPlayerId, DevelopmentAmbiguousWriteFault.QaSlateId))
        {
            Assert.NotNull(lease);
            Assert.False(fault.IsArmed);
        }

        Assert.True(fault.IsArmed); // An unsuccessful upstream request releases, rather than consumes, the fault.
    }

    [Fact]
    public void SuccessfulFaultConsumptionIsAtomicAndCannotBeRepeated()
    {
        var fault = DevelopmentAmbiguousWriteFault.FromConfiguration(true, "submit-picks", "yes");
        using var lease = fault.TryBegin(DevelopmentWriteOperation.SubmitPicks,
            DevelopmentAmbiguousWriteFault.QaPlayerId, DevelopmentAmbiguousWriteFault.QaSlateId);

        Assert.NotNull(lease);
        Assert.True(lease.Consume());
        Assert.False(lease.Consume());
        Assert.False(fault.IsArmed);
        Assert.Null(fault.TryBegin(DevelopmentWriteOperation.SubmitPicks,
            DevelopmentAmbiguousWriteFault.QaPlayerId, DevelopmentAmbiguousWriteFault.QaSlateId));
    }

    [Fact]
    public void ConcurrentMatchingRequestsCanAcquireOnlyOneFaultLease()
    {
        var fault = DevelopmentAmbiguousWriteFault.FromConfiguration(true, "save-draft", "yes");
        var acquired = new ConcurrentBag<DevelopmentAmbiguousWriteFault.Lease>();

        Parallel.For(0, 64, _ =>
        {
            var lease = fault.TryBegin(DevelopmentWriteOperation.SaveDraft,
                DevelopmentAmbiguousWriteFault.QaPlayerId, DevelopmentAmbiguousWriteFault.QaSlateId);
            if (lease is not null) acquired.Add(lease);
        });

        Assert.Single(acquired);
        foreach (var lease in acquired) lease.Dispose();
        Assert.True(fault.IsArmed);
    }
}
