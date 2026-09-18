using Jellyfin.Plugin.Sentinel.Diagnostics;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Diagnostics;

public class KnownCoreIssuesTests
{
    [Fact]
    public void Match_ReturnsIssue_ForTranscodeReasonMissing()
    {
        var match = KnownCoreIssues.Match("TRANSCODE_REASON_MISSING");

        Assert.NotNull(match);
        Assert.Equal("https://github.com/jellyfin/jellyfin/issues/12193", match!.IssueUrl);
    }

    [Fact]
    public void Match_ReturnsNull_ForUnknownCode()
    {
        var match = KnownCoreIssues.Match("SOME_UNRELATED_CODE");

        Assert.Null(match);
    }
}
