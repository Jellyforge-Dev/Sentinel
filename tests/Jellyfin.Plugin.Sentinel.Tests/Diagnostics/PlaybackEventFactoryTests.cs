using System;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Domain;
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
            AudioCodec = "eac3",
            CapturedAtTimestamp = TimeProvider.System.GetTimestamp()
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
    public void FromEventArgs_ForcesTranscodePlayMethod_WhenTranscodeReasonsWereCapturedButPlayMethodContradictsThem()
    {
        // Regression test for a bug confirmed on a real server (v0.1.3.0): a session with a
        // verified, active ffmpeg transcode (video re-encoded to AV1, audio downmixed to 2
        // channels — confirmed via the server's own transcode log) still produced a
        // PlaybackEvent row with PlayMethod = DirectPlay next to a correctly non-zero
        // TranscodeReasons (AudioChannelsNotSupported) in the very same row. The snapshot below
        // reproduces that exact contradiction: real transcode reasons/codec, but a PlayMethod
        // that says DirectPlay (most likely a later progress tick, during a mid-playback seek or
        // audio-track change, reporting a value the cache's null-only merge protection didn't
        // guard against). DirectPlay specifically means no ffmpeg job ran at all, so it cannot
        // legitimately coexist with captured transcode reasons — unlike DirectStream, see the
        // sibling test below.
        var sessionManager = new Mock<ISessionManager>();
        var session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session-contradiction",
            Client = "Jellyfin iOS",
            DeviceName = "iPhone",
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
            PlayMethod = PlayMethod.DirectPlay,
            TranscodeReasons = TranscodeReason.AudioChannelsNotSupported,
            VideoCodec = "av1",
            AudioCodec = null,
            CapturedAtTimestamp = TimeProvider.System.GetTimestamp()
        };

        var result = PlaybackEventFactory.FromEventArgs(args, snapshot);

        Assert.NotNull(result);
        Assert.Equal(PlayMethod.Transcode, result!.PlayMethod);
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.AudioChannelsNotSupported));
        Assert.Equal("av1", result.VideoCodec);
    }

    [Fact]
    public void FromEventArgs_DoesNotForceTranscode_WhenPlayMethodIsDirectStream()
    {
        // A remux (container changed, audio/video streams copied without re-encoding) is
        // reported by Jellyfin as PlayMethod = DirectStream, but it still runs an ffmpeg job and
        // still records the reason the container needed changing (e.g. ContainerNotSupported) on
        // that same session — unlike DirectPlay (see the sibling test above), this is a
        // legitimate combination, not a contradiction. Forcing PlayMethod to Transcode here would
        // misclassify every remux as a full transcode and fire CONTAINER_UNSUPPORTED (Confirmed
        // confidence) for sessions that never re-encoded anything.
        var sessionManager = new Mock<ISessionManager>();
        var session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session-remux",
            Client = "Jellyfin Web",
            DeviceName = "Firefox",
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
            PlayMethod = PlayMethod.DirectStream,
            TranscodeReasons = TranscodeReason.ContainerNotSupported,
            VideoCodec = "h264",
            AudioCodec = "aac",
            CapturedAtTimestamp = TimeProvider.System.GetTimestamp()
        };

        var result = PlaybackEventFactory.FromEventArgs(args, snapshot);

        Assert.NotNull(result);
        Assert.Equal(PlayMethod.DirectStream, result!.PlayMethod);
        Assert.True(result.TranscodeReasons.HasFlag(TranscodeReason.ContainerNotSupported));
    }

    [Fact]
    public void FromEventArgs_FallsBackToSessionData_WhenNoSnapshotWasCaptured()
    {
        // This is a defensive-safety test, not a recovery-scenario test: in real Jellyfin, a
        // session with no snapshot has ALSO had its PlayState/TranscodingInfo cleared by the
        // same RemoveNowPlayingItem call the snapshot mechanism exists to work around (see
        // PlaybackProgressSnapshot's remarks), so the session fields below (deliberately left
        // live, unlike that real scenario) are not what a production handler would actually see.
        // This only proves the null-coalescing fallback doesn't crash and reads the fields it's
        // supposed to when they ARE present — not that the fallback recovers useful data live.
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
    public void FromEventArgs_ReadsUserNameFromSession()
    {
        // UserName, like Client/DeviceName, is never cleared by Jellyfin before firing
        // PlaybackStopped, so it's read directly off the session rather than through the
        // progress-snapshot mechanism (see PlaybackEventFactory's own remarks).
        var sessionManager = new Mock<ISessionManager>();
        var session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
        {
            Id = "session-username",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            UserName = "Alice",
            PlayState = new PlayerStateInfo(),
            TranscodingInfo = null
        };

        var args = new PlaybackStopEventArgs
        {
            Session = session,
            Item = new Movie { Id = Guid.NewGuid() }
        };

        var result = PlaybackEventFactory.FromEventArgs(args, progressSnapshot: null);

        Assert.NotNull(result);
        Assert.Equal("Alice", result!.UserName);
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
