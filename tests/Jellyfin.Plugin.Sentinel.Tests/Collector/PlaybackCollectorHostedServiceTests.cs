using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Collector;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Collector;

public class PlaybackCollectorHostedServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;

    public PlaybackCollectorHostedServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-collector-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
    }

    private PlaybackCollectorHostedService CreateService(Mock<ISessionManager> sessionManagerMock)
    {
        var playbackEventRepository = new PlaybackEventRepository(_database);
        var diagnosisRepository = new DiagnosisRepository(_database, NullLogger<DiagnosisRepository>.Instance);
        var ruleEngine = new RuleEngine(CoreTranscodeRules.All);

        return new PlaybackCollectorHostedService(
            sessionManagerMock.Object,
            ruleEngine,
            playbackEventRepository,
            diagnosisRepository,
            NullLogger<PlaybackCollectorHostedService>.Instance);
    }

    private long CountDiagnoses(string code)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Diagnosis WHERE Code = $code;";
        command.Parameters.AddWithValue("$code", code);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task OnPlaybackStopped_UsesProgressSnapshot_WhenSessionStateWasClearedBeforeStopFired()
    {
        // Faithful simulation of real Jellyfin behavior, verified against
        // Emby.Server.Implementations.Session.SessionManager source: Jellyfin mutates the SAME
        // SessionInfo instance in place, and its own OnPlaybackStopped always resets
        // session.PlayState to a brand-new PlayerStateInfo and clears session.TranscodingInfo to
        // null (via RemoveNowPlayingItem) BEFORE firing PlaybackStopped. This test fires
        // PlaybackProgress first (session still carries live PlayState/TranscodingInfo), then
        // mutates the same session to that exact cleared shape before firing PlaybackStopped —
        // matching what a real handler actually receives. Without the fix, this fails: the
        // diagnosis requires PlayMethod == Transcode, and a handler reading only the (now
        // cleared) session at stop time would see PlayMethod as null.
        var sessionManagerMock = new Mock<ISessionManager>();
        var service = CreateService(sessionManagerMock);

        await service.StartAsync(CancellationToken.None);

        var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-42",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported
            }
        };

        var item = new Movie { Id = Guid.NewGuid() };

        var progressArgs = new PlaybackProgressEventArgs
        {
            Session = session,
            Item = item,
            PlaySessionId = "play-session-42"
        };

        sessionManagerMock.Raise(m => m.PlaybackProgress += null, sessionManagerMock.Object, progressArgs);

        // Simulate Jellyfin's own RemoveNowPlayingItem on the SAME session object, exactly as
        // the real SessionManager.OnPlaybackStopped does before firing PlaybackStopped.
        session.PlayState = new PlayerStateInfo();
        session.TranscodingInfo = null;

        var stopArgs = new PlaybackStopEventArgs
        {
            Session = session,
            Item = item,
            PlaySessionId = "play-session-42"
        };

        sessionManagerMock.Raise(m => m.PlaybackStopped += null, sessionManagerMock.Object, stopArgs);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));
    }

    [Fact]
    public async Task OnPlaybackStopped_StillPersistsPlaybackEvent_WhenNoProgressWasEverObserved()
    {
        // Defensive path: a session that stops without any prior PlaybackProgress (e.g. an
        // immediate failure) must not crash the handler, even though no diagnosis is expected.
        var sessionManagerMock = new Mock<ISessionManager>();
        var service = CreateService(sessionManagerMock);

        await service.StartAsync(CancellationToken.None);

        var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-no-progress",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo(),
            TranscodingInfo = null
        };

        var stopArgs = new PlaybackStopEventArgs
        {
            Session = session,
            Item = new Movie { Id = Guid.NewGuid() },
            PlaySessionId = "play-session-no-progress"
        };

        sessionManagerMock.Raise(m => m.PlaybackStopped += null, sessionManagerMock.Object, stopArgs);

        await service.StopAsync(CancellationToken.None);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlaybackEvent WHERE SessionId = 'session-no-progress';";
        var count = (long)command.ExecuteScalar()!;

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OnPlaybackStopped_DoesNotReuseSnapshot_ForALaterUnrelatedStopWithTheSamePlaySessionId()
    {
        // Regression test for a cache-keying bug found on review: a snapshot must be consumed
        // exactly once. If TryRemove on the first stop failed to actually clear the entry, a
        // later, unrelated stop that happens to reuse the same PlaySessionId (or, before this
        // fix, the same device-scoped Session.Id) would silently inherit stale transcode data
        // that belongs to a completely different playback.
        var sessionManagerMock = new Mock<ISessionManager>();
        var service = CreateService(sessionManagerMock);

        await service.StartAsync(CancellationToken.None);

        var firstSession = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-shared-device",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported
            }
        };
        var firstItem = new Movie { Id = Guid.NewGuid() };

        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = firstSession, Item = firstItem, PlaySessionId = "play-session-reused" });

        firstSession.PlayState = new PlayerStateInfo();
        firstSession.TranscodingInfo = null;

        sessionManagerMock.Raise(
            m => m.PlaybackStopped += null,
            sessionManagerMock.Object,
            new PlaybackStopEventArgs { Session = firstSession, Item = firstItem, PlaySessionId = "play-session-reused" });

        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));

        // A second, unrelated playback stops without ever firing PlaybackProgress, but happens
        // to carry the same PlaySessionId value (the specific value doesn't matter in practice —
        // what matters is that the dictionary entry from the first playback is gone).
        var secondSession = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-shared-device",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo(),
            TranscodingInfo = null
        };
        var secondItem = new Movie { Id = Guid.NewGuid() };

        sessionManagerMock.Raise(
            m => m.PlaybackStopped += null,
            sessionManagerMock.Object,
            new PlaybackStopEventArgs { Session = secondSession, Item = secondItem, PlaySessionId = "play-session-reused" });

        await service.StopAsync(CancellationToken.None);

        // Still exactly 1: the second stop must not have inherited the first playback's cached
        // transcode data.
        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));
    }

    [Fact]
    public async Task OnPlaybackProgress_PreservesTranscodeReasons_WhenALaterTickArrivesWithClearedTranscodingInfo()
    {
        // Regression test for the dispatch-asymmetry race found on review: Jellyfin fires
        // PlaybackProgress inline but dispatches PlaybackStopped asynchronously (verified
        // against real source — see PlaybackCollectorHostedService's class remarks), and its own
        // once-a-second automatic progress timer can replay a client's last known progress after
        // TranscodingInfo has already been cleared for that session. A second progress tick that
        // carries no TranscodingInfo must not erase transcode reasons/codecs already captured by
        // an earlier tick for the same play session.
        var sessionManagerMock = new Mock<ISessionManager>();
        var service = CreateService(sessionManagerMock);

        await service.StartAsync(CancellationToken.None);

        var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-racey",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported,
                VideoCodec = "hevc"
            }
        };
        var item = new Movie { Id = Guid.NewGuid() };

        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = session, Item = item, PlaySessionId = "play-session-racey" });

        // A ghost/duplicate tick for the same play session: PlayMethod still reports Transcode
        // (Jellyfin would still be replaying the client's last submitted progress info), but
        // TranscodingInfo has already been cleared on the session object.
        session.TranscodingInfo = null;

        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = session, Item = item, PlaySessionId = "play-session-racey" });

        session.PlayState = new PlayerStateInfo();

        sessionManagerMock.Raise(
            m => m.PlaybackStopped += null,
            sessionManagerMock.Object,
            new PlaybackStopEventArgs { Session = session, Item = item, PlaySessionId = "play-session-racey" });

        await service.StopAsync(CancellationToken.None);

        // A correct merge yields VIDEO_CODEC_UNSUPPORTED (Confirmed). A naive replace-on-write
        // would instead yield TRANSCODE_REASON_MISSING (Unknown) because the second tick's empty
        // TranscodeReasons would have overwritten the first tick's real ones.
        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));
        Assert.Equal(0, CountDiagnoses("TRANSCODE_REASON_MISSING"));
    }

    public void Dispose()
    {
        // Clear all pooled SQLite connections to release file handles on Windows
        // where the connection pool holds a file handle even after disposal
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
