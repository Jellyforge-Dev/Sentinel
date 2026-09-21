using System;
using System.IO;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Persistence;

public class SentinelDatabaseTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;

    public SentinelDatabaseTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
    }

    [Fact]
    public void InsertAndReadBack_PlaybackEventAndDiagnosis_RoundTrips()
    {
        var playbackEventRepository = new PlaybackEventRepository(_database);
        var diagnosisRepository = new DiagnosisRepository(_database, new LocalizationService(), NullLogger<DiagnosisRepository>.Instance);

        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-1",
            ItemId = "item-1",
            Client = "Fire TV",
            DeviceName = "Living Room TV",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            VideoCodec = "hevc",
            AudioCodec = "eac3",
            SubtitleFormat = null,
            CreatedAtUtc = DateTime.UtcNow
        };

        var playbackEventId = playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "TranscodeReasons = VideoCodecNotSupported" }
        };

        diagnosisRepository.Insert(playbackEventId, diagnosis);

        var stored = diagnosisRepository.GetAllForPlaybackEvent(playbackEventId);

        Assert.Single(stored);
        Assert.Equal("VIDEO_CODEC_UNSUPPORTED", stored[0].Code);
        Assert.Equal("Confirmed", stored[0].Confidence);
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
