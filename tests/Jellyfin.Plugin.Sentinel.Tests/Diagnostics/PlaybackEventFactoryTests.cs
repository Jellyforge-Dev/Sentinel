using System;
using Jellyfin.Plugin.Sentinel.Collector;
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
    public void FromEventArgs_PrefersProgressSnapshot_OverSessionData()
    {
        // This is the regression test for the real bug this parameter exists to fix: Jellyfin
        // always clears Session.PlayState/TranscodingInfo before firing PlaybackStopped (verified
        // against real Jellyfin 12.1 source — Emby.Server.Implementations.Session.SessionManager's
        // OnPlaybackStopped calls RemoveNowPlayingItem, which resets PlayState to a brand-new
        // PlayerStateInfo and clears TranscodingInfo, before the event fires). The session below
        // therefore has that exact cleared shape — empty PlayState, null TranscodingInfo — matching
        // what a real PlaybackStopped handler actually receives, while the snapshot carries what
        // was captured earlier from PlaybackProgress. The result must come from the snapshot.
        var sessionManager = new Mock<ISessionManager>();
        var session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session-1",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo(),
            TranscodingInfo = null
        };

        var args = new PlaybackStopEventArgs
        {
            Session = session,
            Item = new Movie { Id = Guid.NewGuid() }
        };

        var snapshot = new PlaybackProgressSnapshot
        {
            PlayMethod = PlayMethod.Transcode,
            TranscodeReasons = TranscodeReason.SubtitleCodecNotSupported | TranscodeReason.VideoCodecNotSupported,
            VideoCodec = "hevc",
            AudioCodec = "eac3"
        };

        var result = PlaybackEventFactory.FromEventArgs(args, snapshot);

        Assert.NotNull(result);
        Assert.Equal("session-1", result!.SessionId);
        Assert.Equal("Fire TV", result.Client);
        Assert.Equal("Living Room TV", result.DeviceName);
        Assert.Equal(PlayMethod.Transcode, result.PlayMethod);
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.VideoCodecNotSupported));
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.SubtitleCodecNotSupported));
        Assert.Equal("hevc", result.VideoCodec);
        Assert.Equal("eac3", result.AudioCodec);
    }

    [Fact]
    public void FromEventArgs_FallsBackToSessionData_WhenNoSnapshotWasCaptured()
    {
        // Defensive fallback for the rare case a session stops before any PlaybackProgress ever
        // fired for it (e.g. an immediate playback failure) — degraded but not crashed.
        var sessionManager = new Mock<ISessionManager>();
        var session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session-1",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported,
                VideoCodec = "hevc"
            }
        };

        var args = new PlaybackStopEventArgs
        {
            Session = session,
            Item = new Movie { Id = Guid.NewGuid() }
        };

        var result = PlaybackEventFactory.FromEventArgs(args, progressSnapshot: null);

        Assert.NotNull(result);
        Assert.Equal(PlayMethod.Transcode, result!.PlayMethod);
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.VideoCodecNotSupported));
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

        var result = PlaybackEventFactory.FromEventArgs(args, progressSnapshot: null);

        Assert.Null(result);
    }
}
