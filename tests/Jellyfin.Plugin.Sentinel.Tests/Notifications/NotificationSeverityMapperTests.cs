using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Notifications;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Notifications;

public class NotificationSeverityMapperTests
{
    [Theory]
    [InlineData(Confidence.Confirmed, "critical")]
    [InlineData(Confidence.VeryLikely, "important")]
    [InlineData(Confidence.Likely, "warning")]
    [InlineData(Confidence.Possible, "notice")]
    [InlineData(Confidence.Unknown, "info")]
    public void FromConfidence_MapsToExpectedSeverity(Confidence confidence, string expectedSeverity)
    {
        Assert.Equal(expectedSeverity, NotificationSeverityMapper.FromConfidence(confidence));
    }

    [Theory]
    [InlineData("info", "info", true)]
    [InlineData("info", "notice", false)]
    [InlineData("info", "critical", false)]
    [InlineData("notice", "info", true)]
    [InlineData("warning", "notice", true)]
    [InlineData("important", "warning", true)]
    [InlineData("critical", "important", true)]
    [InlineData("critical", "info", true)]
    [InlineData("critical", "critical", true)]
    [InlineData("bogus", "info", true)]
    [InlineData("critical", "bogus", true)]
    [InlineData("bogus", "notice", false)]
    public void MeetsThreshold_RanksCorrectly_AndTreatsUnrecognizedValuesAsInfo(string severity, string minimumSeverity, bool expected)
    {
        Assert.Equal(expected, NotificationSeverityMapper.MeetsThreshold(severity, minimumSeverity));
    }
}
