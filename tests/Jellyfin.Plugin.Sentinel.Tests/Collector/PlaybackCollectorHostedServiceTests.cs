using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Collector;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Notifications;
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

    /// <summary>
    /// A controllable <see cref="TimeProvider"/> for TTL-eviction tests: one unit advanced is one
    /// millisecond, so <see cref="Advance"/> can move the clock by exact, arbitrary spans without
    /// depending on wall-clock time or real delays.
    /// </summary>
    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan delta) => _timestamp += (long)delta.TotalMilliseconds;
    }

    private PlaybackCollectorHostedService CreateService(Mock<ISessionManager> sessionManagerMock, TimeProvider? timeProvider = null)
    {
        var playbackEventRepository = new PlaybackEventRepository(_database);
        var diagnosisRepository = new DiagnosisRepository(_database, new LocalizationService(), NullLogger<DiagnosisRepository>.Instance);
        var incidentRepository = new IncidentRepository(_database);
        var ruleEngine = new RuleEngine(CoreTranscodeRules.All);
        var notificationDispatcher = new NotificationDispatcher(
            new Mock<IHttpClientFactory>().Object,
            NullLoggerFactory.Instance,
            new LocalizationService(),
            NullLogger<NotificationDispatcher>.Instance);

        return new PlaybackCollectorHostedService(
            sessionManagerMock.Object,
            ruleEngine,
            playbackEventRepository,
            diagnosisRepository,
            incidentRepository,
            notificationDispatcher,
            timeProvider ?? TimeProvider.System,
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

    private long CountDiagnosesForItem(string code, string itemId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM Diagnosis d
            JOIN PlaybackEvent p ON p.Id = d.PlaybackEventId
            WHERE d.Code = $code AND p.ItemId = $itemId;
            """;
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$itemId", itemId);
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
    public async Task OnPlaybackStopped_DoesNotAttributeUnrelatedSnapshot_ToADifferentPlaySessionOnTheSameDevice()
    {
        // Regression test for the original cache-keying bug: caching by the device-scoped
        // Session.Id (instead of the per-playback PlaySessionId) let a snapshot from one
        // playback get attributed to a later, unrelated playback on the same device.
        //
        // The first playback below is DELIBERATELY never stopped — it only ever fires
        // PlaybackProgress, simulating a client that crashed or force-quit without ever sending
        // a stop report. This is what actually distinguishes the two keying schemes: if the
        // first playback were stopped first (as an earlier version of this test did), TryRemove
        // would clear its cache entry regardless of which key was used, and the second stop would
        // find nothing either way — passing for the wrong reason under BOTH the buggy
        // Session.Id-keyed cache and the fixed PlaySessionId-keyed one. Leaving the first
        // playback's entry in place is what makes a Session.Id-keyed cache actually reachable by
        // the second, unrelated stop below.
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
            new PlaybackProgressEventArgs { Session = firstSession, Item = firstItem, PlaySessionId = "play-session-first" });

        // A second playback on the SAME device, with its OWN distinct PlaySessionId, stops
        // without ever firing PlaybackProgress for it and with already-cleared session fields —
        // exactly what a real PlaybackStopped handler would see for a genuinely new playback.
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
            new PlaybackStopEventArgs { Session = secondSession, Item = secondItem, PlaySessionId = "play-session-second" });

        await service.StopAsync(CancellationToken.None);

        // The first playback's own PlaybackProgress now legitimately produces a live diagnosis
        // for ITS item (see PlaybackCollectorHostedService's live-detection remarks) — that one
        // diagnosis existing is correct and expected. What this test actually guards against is
        // the second, unrelated stop inheriting that cached data: it must never be attributed to
        // secondItem just because it shares a device with the first, still-uncompleted playback.
        Assert.Equal(1, CountDiagnosesForItem("VIDEO_CODEC_UNSUPPORTED", firstItem.Id.ToString()));
        Assert.Equal(0, CountDiagnosesForItem("VIDEO_CODEC_UNSUPPORTED", secondItem.Id.ToString()));
    }

    [Fact]
    public async Task OnPlaybackStopped_ResolvesSnapshot_ViaSessionId_WhenStopHasNoPlaySessionId()
    {
        // Regression test for a bug found on review: Jellyfin's own idle-timeout stop path
        // (SessionManager.CheckForIdlePlayback — verified against the exact v12.1 tag Sentinel
        // targets) builds its PlaybackStopInfo without ever setting PlaySessionId, so
        // PlaybackStopEventArgs.PlaySessionId is null for every idle-timeout stop. That is
        // exactly the "client crashed mid-transcode, Jellyfin noticed it stopped reporting"
        // case this plugin exists to diagnose. Keying the cache by PlaySessionId alone would
        // silently produce zero diagnoses for every one of these — the secondary Session.Id
        // index exists specifically to still resolve them.
        var sessionManagerMock = new Mock<ISessionManager>();
        var service = CreateService(sessionManagerMock);

        await service.StartAsync(CancellationToken.None);

        var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-idle-timeout",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported
            }
        };
        var item = new Movie { Id = Guid.NewGuid() };

        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = session, Item = item, PlaySessionId = "play-session-idle" });

        session.PlayState = new PlayerStateInfo();
        session.TranscodingInfo = null;

        // Mirrors SessionManager.CheckForIdlePlayback's own PlaybackStopInfo construction: no
        // PlaySessionId set at all, only Session.Id (via args.Session).
        sessionManagerMock.Raise(
            m => m.PlaybackStopped += null,
            sessionManagerMock.Object,
            new PlaybackStopEventArgs { Session = session, Item = item, PlaySessionId = null });

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));
    }

    [Fact]
    public async Task OnPlaybackProgress_PreservesFullSnapshot_WhenALaterTickArrivesWithClearedSessionState()
    {
        // Regression test for the dispatch-asymmetry race found on review: Jellyfin fires
        // PlaybackProgress inline but dispatches PlaybackStopped asynchronously (verified
        // against real source — see PlaybackCollectorHostedService's class remarks), so a
        // progress tick can still land after RemoveNowPlayingItem has already reset
        // session.PlayState and cleared session.TranscodingInfo for a session that's about to
        // stop, but before the queued stop handler consumes the cached snapshot. A tick in that
        // state must not erase PlayMethod, TranscodeReasons, or codecs already captured by an
        // earlier tick for the same play session.
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

        // Simulate RemoveNowPlayingItem having already run on this SAME session instance before
        // this second, ghost/duplicate progress tick lands — both PlayState AND TranscodingInfo
        // cleared, matching the exact real-world race window.
        session.PlayState = new PlayerStateInfo();
        session.TranscodingInfo = null;

        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = session, Item = item, PlaySessionId = "play-session-racey" });

        sessionManagerMock.Raise(
            m => m.PlaybackStopped += null,
            sessionManagerMock.Object,
            new PlaybackStopEventArgs { Session = session, Item = item, PlaySessionId = "play-session-racey" });

        await service.StopAsync(CancellationToken.None);

        // A correct merge (including PlayMethod) yields VIDEO_CODEC_UNSUPPORTED (Confirmed). A
        // merge that replaces PlayMethod unconditionally would instead yield no diagnosis at
        // all, since every rule in CoreTranscodeRules requires PlayMethod == Transcode.
        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));
        Assert.Equal(0, CountDiagnoses("TRANSCODE_REASON_MISSING"));
    }

    [Fact]
    public async Task OnPlaybackProgress_EvictsSnapshot_OnceItsTtlHasElapsed()
    {
        // Confirms the TTL sweep actually removes entries for play sessions that never fire
        // PlaybackStopped (a crashed client, a dropped connection) — otherwise, switching the
        // cache key from the device-bounded Session.Id to the per-playback PlaySessionId (see
        // the other tests above) would make the cache's keyspace grow without bound for as long
        // as the server runs.
        var sessionManagerMock = new Mock<ISessionManager>();
        var timeProvider = new TestTimeProvider();
        var service = CreateService(sessionManagerMock, timeProvider);

        await service.StartAsync(CancellationToken.None);

        var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-ttl",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported
            }
        };
        var item = new Movie { Id = Guid.NewGuid() };

        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = session, Item = item, PlaySessionId = "play-session-ttl" });

        timeProvider.Advance(TimeSpan.FromMinutes(31));

        // Any other progress tick runs the eviction sweep — it doesn't need to relate to the
        // session under test.
        var otherSession = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance) { Id = "session-other" };
        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = otherSession, Item = item, PlaySessionId = "play-session-other" });

        session.PlayState = new PlayerStateInfo();
        session.TranscodingInfo = null;

        sessionManagerMock.Raise(
            m => m.PlaybackStopped += null,
            sessionManagerMock.Object,
            new PlaybackStopEventArgs { Session = session, Item = item, PlaySessionId = "play-session-ttl" });

        await service.StopAsync(CancellationToken.None);

        // The PlaybackProgress above now legitimately live-diagnoses this session immediately
        // (see PlaybackCollectorHostedService's live-detection remarks), so exactly one diagnosis
        // is correct and expected. What this asserts is that it stays at exactly one: if the TTL
        // sweep had NOT evicted the stale snapshot, the stop handler could have re-attributed it
        // and produced a second, duplicate diagnosis instead of correctly finding no further
        // transcode data once the session's own fields were cleared.
        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));
    }

    [Fact]
    public async Task OnPlaybackStopped_UpsertsSingleIncident_WhenTheSameDiagnosisFiresTwice()
    {
        // The Incident Engine must dedup diagnoses sharing the same fingerprint (code, item,
        // client, device) into one tracked incident with an incrementing occurrence count,
        // rather than creating a new incident row for every raw diagnosis.
        var sessionManagerMock = new Mock<ISessionManager>();
        var incidentRepository = new IncidentRepository(_database);
        var service = CreateService(sessionManagerMock);

        await service.StartAsync(CancellationToken.None);

        var itemId = Guid.NewGuid();
        var item = new Movie { Id = itemId };

        void FireDiagnosablePlayback(string sessionId, string playSessionId)
        {
            var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
            {
                Id = sessionId,
                Client = "Fire TV",
                DeviceName = "Living Room TV",
                PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
                TranscodingInfo = new TranscodingInfo
                {
                    TranscodeReasons = TranscodeReason.VideoCodecNotSupported
                }
            };

            sessionManagerMock.Raise(
                m => m.PlaybackProgress += null,
                sessionManagerMock.Object,
                new PlaybackProgressEventArgs { Session = session, Item = item, PlaySessionId = playSessionId });

            // Simulate Jellyfin's own RemoveNowPlayingItem before PlaybackStopped fires (see
            // class remarks on PlaybackCollectorHostedService).
            session.PlayState = new PlayerStateInfo();
            session.TranscodingInfo = null;

            sessionManagerMock.Raise(
                m => m.PlaybackStopped += null,
                sessionManagerMock.Object,
                new PlaybackStopEventArgs { Session = session, Item = item, PlaySessionId = playSessionId });
        }

        FireDiagnosablePlayback("session-incident-1", "play-session-incident-1");
        FireDiagnosablePlayback("session-incident-2", "play-session-incident-2");

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));

        var incidents = incidentRepository.GetRecent(50);
        var matching = incidents.Where(incident =>
            incident.Code == "VIDEO_CODEC_UNSUPPORTED"
            && incident.ItemId == itemId.ToString()
            && incident.Client == "Fire TV"
            && incident.DeviceName == "Living Room TV").ToList();

        var incident = Assert.Single(matching);
        Assert.Equal(2, incident.OccurrenceCount);
    }

    [Fact]
    public async Task OnPlaybackProgress_DiagnosesLive_WhileThePlaybackIsStillOngoing()
    {
        // The headline behavior a live-server admin actually asked for: a transcode must be
        // detected (and an Incident created) while playback is still running, not only after the
        // user stops it.
        var sessionManagerMock = new Mock<ISessionManager>();
        var incidentRepository = new IncidentRepository(_database);
        var service = CreateService(sessionManagerMock);

        await service.StartAsync(CancellationToken.None);

        var item = new Movie { Id = Guid.NewGuid() };
        var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-live",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported
            }
        };

        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = session, Item = item, PlaySessionId = "play-session-live" });

        // Deliberately never firing PlaybackStopped — the playback is still ongoing.
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));

        var incident = Assert.Single(incidentRepository.GetRecent(50));
        Assert.Equal("VIDEO_CODEC_UNSUPPORTED", incident.Code);
        Assert.Equal(1, incident.OccurrenceCount);
    }

    [Fact]
    public async Task OnPlaybackStopped_DoesNotDuplicateDiagnosis_ForCodeAlreadyRecordedLive()
    {
        // If the condition that was already live-diagnosed during PlaybackProgress is still true
        // when PlaybackStopped fires (the transcode never stopped needing that reason), the stop
        // handler must skip it rather than diagnosing — and dispatching a notification for — the
        // exact same condition a second time.
        var sessionManagerMock = new Mock<ISessionManager>();
        var service = CreateService(sessionManagerMock);

        await service.StartAsync(CancellationToken.None);

        var item = new Movie { Id = Guid.NewGuid() };
        var session = new SessionInfo(sessionManagerMock.Object, NullLogger.Instance)
        {
            Id = "session-live-then-stop",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayState = new PlayerStateInfo { PlayMethod = PlayMethod.Transcode },
            TranscodingInfo = new TranscodingInfo
            {
                TranscodeReasons = TranscodeReason.VideoCodecNotSupported
            }
        };

        sessionManagerMock.Raise(
            m => m.PlaybackProgress += null,
            sessionManagerMock.Object,
            new PlaybackProgressEventArgs { Session = session, Item = item, PlaySessionId = "play-session-live-then-stop" });

        // Unlike the real Jellyfin RemoveNowPlayingItem sequence, the session fields are left
        // exactly as they were — simulating a stop report that (unusually) still carries the same
        // transcode data, the scenario most likely to double-diagnose without the live-recorded
        // skip guard.
        sessionManagerMock.Raise(
            m => m.PlaybackStopped += null,
            sessionManagerMock.Object,
            new PlaybackStopEventArgs { Session = session, Item = item, PlaySessionId = "play-session-live-then-stop" });

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, CountDiagnoses("VIDEO_CODEC_UNSUPPORTED"));
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
