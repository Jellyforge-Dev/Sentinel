using System;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Diagnostics;

public class PlaybackEventFactoryTests
{
    [Fact]
    public void FromEventArgs_MapsTranscodeReasonsAndClientFromSession()
    {
        var sessionManager = new Mock<ISessionManager>();
        var session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session-1",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.SubtitleCodecNotSupported | TranscodeReason.VideoCodecNotSupported,
                VideoCodec = "hevc",
                AudioCodec = "eac3"
            }
        };

        var args = new PlaybackStopEventArgs
        {
            Session = session,
            Item = new Movie { Id = Guid.NewGuid() }
        };

        var result = PlaybackEventFactory.FromEventArgs(args);

        Assert.NotNull(result);
        Assert.Equal("session-1", result!.SessionId);
        Assert.Equal("Fire TV", result.Client);
        Assert.Equal("Living Room TV", result.DeviceName);
        Assert.Equal(PlayMethod.Transcode, result.PlayMethod);
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.VideoCodecNotSupported));
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.SubtitleCodecNotSupported));
        Assert.Equal("hevc", result.VideoCodec);
    }

    [Fact]
    public void FromEventArgs_ReturnsNull_WhenSessionIsMissing()
    {
        var args = new PlaybackStopEventArgs
        {
            Session = null,
            Item = new Movie { Id = Guid.NewGuid() }
        };

        var result = PlaybackEventFactory.FromEventArgs(args);

        Assert.Null(result);
    }
}
