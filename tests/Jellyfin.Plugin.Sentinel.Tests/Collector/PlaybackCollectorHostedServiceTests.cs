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
        var playbackEventRepository = new PlaybackEventRepository(_database);
        var diagnosisRepository = new DiagnosisRepository(_database, NullLogger<DiagnosisRepository>.Instance);
        var ruleEngine = new RuleEngine(CoreTranscodeRules.All);

        var service = new PlaybackCollectorHostedService(
            sessionManagerMock.Object,
            ruleEngine,
            playbackEventRepository,
            diagnosisRepository,
            NullLogger<PlaybackCollectorHostedService>.Instance);

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
            Item = item
        };

        sessionManagerMock.Raise(m => m.PlaybackProgress += null, sessionManagerMock.Object, progressArgs);

        // Simulate Jellyfin's own RemoveNowPlayingItem on the SAME session object, exactly as
        // the real SessionManager.OnPlaybackStopped does before firing PlaybackStopped.
        session.PlayState = new PlayerStateInfo();
        session.TranscodingInfo = null;

        var stopArgs = new PlaybackStopEventArgs
        {
            Session = session,
            Item = item
        };

        sessionManagerMock.Raise(m => m.PlaybackStopped += null, sessionManagerMock.Object, stopArgs);

        await service.StopAsync(CancellationToken.None);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Diagnosis WHERE Code = 'VIDEO_CODEC_UNSUPPORTED';";
        var count = (long)command.ExecuteScalar()!;

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OnPlaybackStopped_StillPersistsPlaybackEvent_WhenNoProgressWasEverObserved()
    {
        // Defensive path: a session that stops without any prior PlaybackProgress (e.g. an
        // immediate failure) must not crash the handler, even though no diagnosis is expected.
        var sessionManagerMock = new Mock<ISessionManager>();
        var playbackEventRepository = new PlaybackEventRepository(_database);
        var diagnosisRepository = new DiagnosisRepository(_database, NullLogger<DiagnosisRepository>.Instance);
        var ruleEngine = new RuleEngine(CoreTranscodeRules.All);

        var service = new PlaybackCollectorHostedService(
            sessionManagerMock.Object,
            ruleEngine,
            playbackEventRepository,
            diagnosisRepository,
            NullLogger<PlaybackCollectorHostedService>.Instance);

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
            Item = new Movie { Id = Guid.NewGuid() }
        };

        sessionManagerMock.Raise(m => m.PlaybackStopped += null, sessionManagerMock.Object, stopArgs);

        await service.StopAsync(CancellationToken.None);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlaybackEvent WHERE SessionId = 'session-no-progress';";
        var count = (long)command.ExecuteScalar()!;

        Assert.Equal(1, count);
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
