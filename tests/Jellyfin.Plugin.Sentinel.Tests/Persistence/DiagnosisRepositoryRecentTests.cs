using System;
using System.IO;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Persistence;

public class DiagnosisRepositoryRecentTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;
    private readonly PlaybackEventRepository _playbackEventRepository;
    private readonly DiagnosisRepository _diagnosisRepository;
    private readonly LocalizationService _localizationService = new();

    public DiagnosisRepositoryRecentTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-recent-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
        _playbackEventRepository = new PlaybackEventRepository(_database);
        _diagnosisRepository = new DiagnosisRepository(_database, _localizationService, NullLogger<DiagnosisRepository>.Instance);
    }

    [Fact]
    public void GetRecent_ProjectsEveryColumn_JoinedWithTheCorrectPlaybackEvent_MostRecentFirst()
    {
        // Insertion order (not wall-clock timing) is what the ordering assertions rely on: the
        // repository's query breaks ties on Id DESC specifically so two diagnoses created within
        // the same DateTime.UtcNow tick (a real possibility on Windows, where timer resolution can
        // exceed the gap between two fast inserts) still sort deterministically by insertion order.
        var olderItemId = Guid.NewGuid().ToString();
        var olderEventId = InsertPlaybackEvent("session-old", "Fire TV", "Living Room", olderItemId, userName: "Bob");
        _diagnosisRepository.Insert(olderEventId, BuildDiagnosis("OLDER_CODE"));

        var newerItemId = Guid.NewGuid().ToString();
        var newerEventId = InsertPlaybackEvent("session-new", "Chromecast", "Kitchen", newerItemId, userName: "Alice");
        _diagnosisRepository.Insert(newerEventId, BuildDiagnosis("NEWER_CODE"));

        var recent = _diagnosisRepository.GetRecent(50, SupportedLanguage.En);

        Assert.Equal(2, recent.Count);

        // Every projected column is asserted here, not just a convenient subset — a future edit
        // that swaps two columns in the SELECT list (e.g. Explanation/Recommendation, or
        // ItemId/Client) must fail one of these, not silently pass.
        var newer = recent[0];
        Assert.True(newer.Id > 0);
        Assert.Equal("NEWER_CODE", newer.Code);
        Assert.Equal(Confidence.Confirmed, newer.Confidence);
        Assert.Equal(new[] { "evidence" }, newer.Evidence);
        Assert.Equal("NEWER_CODE_EXPLANATION", newer.Explanation);
        Assert.Equal("NEWER_CODE_RECOMMENDATION", newer.Recommendation);
        Assert.Equal(newerItemId, newer.ItemId);
        Assert.Equal("Chromecast", newer.Client);
        Assert.Equal("Kitchen", newer.DeviceName);
        Assert.Equal("Alice", newer.UserName);
        Assert.Equal("Transcode", newer.PlayMethod);

        var older = recent[1];
        Assert.Equal("OLDER_CODE", older.Code);
        Assert.Equal(olderItemId, older.ItemId);
        Assert.Equal("Fire TV", older.Client);
        Assert.Equal("Bob", older.UserName);
        Assert.True(older.Id < newer.Id);
    }

    [Fact]
    public void GetRecent_OrdersByCreatedAtUtc_NotJustInsertionOrder()
    {
        // Insert() always stamps DateTime.UtcNow server-side, so the only way to construct a case
        // where CreatedAtUtc and Id disagree (the exact case that proves CreatedAtUtc, not Id, is
        // the primary sort key) is to manipulate the timestamp directly after insertion.
        var firstEventId = InsertPlaybackEvent("session-a", "Fire TV", "Living Room");
        _diagnosisRepository.Insert(firstEventId, BuildDiagnosis("INSERTED_FIRST"));

        var secondEventId = InsertPlaybackEvent("session-b", "Fire TV", "Living Room");
        _diagnosisRepository.Insert(secondEventId, BuildDiagnosis("INSERTED_SECOND"));

        // Back-date the second (higher-Id) diagnosis so it is now the OLDER of the two by
        // CreatedAtUtc, despite having the higher Id.
        SetDiagnosisCreatedAtUtc("INSERTED_SECOND", DateTime.UtcNow.AddDays(-1));

        var recent = _diagnosisRepository.GetRecent(50, SupportedLanguage.En);

        Assert.Equal("INSERTED_FIRST", recent[0].Code);
        Assert.Equal("INSERTED_SECOND", recent[1].Code);
    }

    [Fact]
    public void GetRecent_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            var eventId = InsertPlaybackEvent($"session-{i}", "Fire TV", "Living Room");
            _diagnosisRepository.Insert(eventId, BuildDiagnosis($"CODE_{i}"));
        }

        var recent = _diagnosisRepository.GetRecent(2, SupportedLanguage.En);

        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public void GetRecent_DeserializesEvidenceBackIntoAList()
    {
        var eventId = InsertPlaybackEvent("session-evidence", "Fire TV", "Living Room");
        var diagnosis = new Diagnosis
        {
            Code = "TEST_CODE",
            Confidence = Confidence.Likely,
            Evidence = new[] { "fact one", "fact two" }
        };
        _diagnosisRepository.Insert(eventId, diagnosis);

        var recent = _diagnosisRepository.GetRecent(50, SupportedLanguage.En);

        Assert.Equal(new[] { "fact one", "fact two" }, recent[0].Evidence);
    }

    private long InsertPlaybackEvent(string sessionId, string client, string deviceName, string? itemId = null, string userName = "Alice")
    {
        var playbackEvent = new PlaybackEvent
        {
            SessionId = sessionId,
            ItemId = itemId ?? Guid.NewGuid().ToString(),
            Client = client,
            DeviceName = deviceName,
            UserName = userName,
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            CreatedAtUtc = DateTime.UtcNow
        };

        return _playbackEventRepository.Insert(playbackEvent);
    }

    private void SetDiagnosisCreatedAtUtc(string code, DateTime createdAtUtc)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Diagnosis SET CreatedAtUtc = $createdAtUtc WHERE Code = $code;";
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$code", code);
        command.ExecuteNonQuery();
    }

    private static Diagnosis BuildDiagnosis(string code) => new()
    {
        Code = code,
        Confidence = Confidence.Confirmed,
        Evidence = new[] { "evidence" }
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
