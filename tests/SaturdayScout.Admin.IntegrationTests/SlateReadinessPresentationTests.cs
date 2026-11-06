using SaturdayScout.Admin;
using Xunit;

namespace SaturdayScout.Admin.IntegrationTests;

public sealed class SlateReadinessPresentationTests
{
    [Theory]
    [InlineData("Draft", false, "Incomplete draft")]
    [InlineData("Draft", true, "Ready to publish")]
    [InlineData("Published", false, "Published")]
    [InlineData("Locked", false, "Closed")]
    [InlineData("Finalized", false, "Closed")]
    public void ReadinessLabelReflectsSlateLifecycle(string status, bool readyForPublishing, string expected)
    {
        Assert.Equal(expected, SlateReadinessPresentation.Label(status, readyForPublishing));
    }
}
