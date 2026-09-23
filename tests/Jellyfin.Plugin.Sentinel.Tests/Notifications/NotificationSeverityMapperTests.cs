using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Notifications;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Notifications;

public class NotificationSeverityMapperTests
{
    [Theory]
    [InlineData(Confidence.Confirmed, "high")]
    [InlineData(Confidence.VeryLikely, "high")]
    [InlineData(Confidence.Likely, "medium")]
    [InlineData(Confidence.Possible, "low")]
    [InlineData(Confidence.Unknown, "low")]
    public void FromConfidence_MapsToExpectedSeverity(Confidence confidence, string expectedSeverity)
    {
        Assert.Equal(expectedSeverity, NotificationSeverityMapper.FromConfidence(confidence));
    }

    [Theory]
    [InlineData("low", "low", true)]
    [InlineData("low", "medium", false)]
    [InlineData("low", "high", false)]
    [InlineData("medium", "low", true)]
    [InlineData("medium", "medium", true)]
    [InlineData("medium", "high", false)]
    [InlineData("high", "low", true)]
    [InlineData("high", "medium", true)]
    [InlineData("high", "high", true)]
    [InlineData("bogus", "low", true)]
    [InlineData("high", "bogus", true)]
    [InlineData("bogus", "medium", false)]
    public void MeetsThreshold_RanksCorrectly_AndTreatsUnrecognizedValuesAsLow(string severity, string minimumSeverity, bool expected)
    {
        Assert.Equal(expected, NotificationSeverityMapper.MeetsThreshold(severity, minimumSeverity));
    }
}
