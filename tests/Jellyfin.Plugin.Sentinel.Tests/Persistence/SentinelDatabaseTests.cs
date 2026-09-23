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
            UserName = "Alice",
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

    [Fact]
    public void Initialize_AddsUserNameColumn_ToPreExistingPlaybackEventAndIncidentTables()
    {
        var migrationDbPath = Path.Combine(Path.GetTempPath(), $"sentinel-migration-test-{Guid.NewGuid()}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();

                // The pre-migration schema — no UserName column on either table, matching what a
                // real install created before this change shipped.
                command.CommandText =
                    """
                    CREATE TABLE PlaybackEvent (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SessionId TEXT NOT NULL,
                        ItemId TEXT NOT NULL,
                        Client TEXT NOT NULL,
                        DeviceName TEXT NOT NULL,
                        PlayMethod TEXT NOT NULL,
                        TranscodeReasons INTEGER NOT NULL,
                        VideoCodec TEXT NULL,
                        AudioCodec TEXT NULL,
                        SubtitleFormat TEXT NULL,
                        CreatedAtUtc TEXT NOT NULL
                    );

                    CREATE TABLE Incident (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Code TEXT NOT NULL,
                        ItemId TEXT NOT NULL,
                        Client TEXT NOT NULL,
                        DeviceName TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        OccurrenceCount INTEGER NOT NULL,
                        FirstSeenUtc TEXT NOT NULL,
                        LastSeenUtc TEXT NOT NULL,
                        AcknowledgedAtUtc TEXT NULL,
                        ResolvedAtUtc TEXT NULL
                    );
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            // Constructing SentinelDatabase runs Initialize(), including the guarded
            // AddColumnIfMissing migration — this must not throw against a database that predates
            // the UserName column.
            var migratedDatabase = new SentinelDatabase(migrationDbPath);
            var playbackEventRepository = new PlaybackEventRepository(migratedDatabase);

            var playbackEvent = new PlaybackEvent
            {
                SessionId = "session-migrated",
                ItemId = "item-migrated",
                Client = "Fire TV",
                DeviceName = "Living Room TV",
                UserName = "Alice",
                PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
                TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
                CreatedAtUtc = DateTime.UtcNow
            };

            var playbackEventId = playbackEventRepository.Insert(playbackEvent);

            using (var readConnection = migratedDatabase.OpenConnection())
            using (var readCommand = readConnection.CreateCommand())
            {
                readCommand.CommandText = "SELECT UserName FROM PlaybackEvent WHERE Id = $id;";
                readCommand.Parameters.AddWithValue("$id", playbackEventId);
                Assert.Equal("Alice", (string)readCommand.ExecuteScalar()!);
            }

            // Proving PlaybackEvent's UserName round-trips post-migration doesn't prove Incident's
            // does too, even though AddColumnIfMissing is the identical helper called identically
            // for both tables — Incident has its own INSERT/UPDATE SQL in IncidentRepository,
            // which could independently have an off-by-one or a forgotten column reference that
            // PlaybackEvent's own round-trip would never catch.
            var diagnosisRepository = new DiagnosisRepository(migratedDatabase, new LocalizationService(), NullLogger<DiagnosisRepository>.Instance);
            var diagnosisId = diagnosisRepository.Insert(playbackEventId, new Diagnosis
            {
                Code = "VIDEO_CODEC_UNSUPPORTED",
                Confidence = Confidence.Confirmed,
                Evidence = new[] { "TranscodeReasons = VideoCodecNotSupported" }
            });

            var incidentRepository = new IncidentRepository(migratedDatabase);
            incidentRepository.UpsertOnDiagnosis(diagnosisId, "VIDEO_CODEC_UNSUPPORTED", "item-migrated", "Fire TV", "Living Room TV", "Alice");

            var incidents = incidentRepository.GetRecent(50);
            Assert.Single(incidents);
            Assert.Equal("Alice", incidents[0].UserName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(migrationDbPath))
            {
                File.Delete(migrationDbPath);
            }
        }
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
