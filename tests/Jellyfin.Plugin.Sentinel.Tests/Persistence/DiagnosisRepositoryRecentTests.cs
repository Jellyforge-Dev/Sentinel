using System;
using System.IO;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Persistence;

public class DiagnosisRepositoryRecentTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;
    private readonly PlaybackEventRepository _playbackEventRepository;
    private readonly DiagnosisRepository _diagnosisRepository;

    public DiagnosisRepositoryRecentTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-recent-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
        _playbackEventRepository = new PlaybackEventRepository(_database);
        _diagnosisRepository = new DiagnosisRepository(_database);
    }

    [Fact]
    public void GetRecent_ReturnsDiagnosesJoinedWithPlaybackEventContext_MostRecentFirst()
    {
        // Insertion order (not wall-clock timing) is what the assertions rely on: the repository's
        // query breaks ties on Id DESC specifically so two diagnoses created within the same
        // DateTime.UtcNow tick (a real possibility on Windows, where timer resolution can exceed
        // the gap between two fast inserts) still sort deterministically by insertion order.
        var olderEventId = InsertPlaybackEvent("session-old", "Fire TV", "Living Room");
        _diagnosisRepository.Insert(olderEventId, BuildDiagnosis("OLDER_CODE"));

        var newerEventId = InsertPlaybackEvent("session-new", "Chromecast", "Kitchen");
        _diagnosisRepository.Insert(newerEventId, BuildDiagnosis("NEWER_CODE"));

        var recent = _diagnosisRepository.GetRecent(50);

        Assert.Equal(2, recent.Count);
        Assert.Equal("NEWER_CODE", recent[0].Code);
        Assert.Equal("Chromecast", recent[0].Client);
        Assert.Equal("Kitchen", recent[0].DeviceName);
        Assert.Equal("OLDER_CODE", recent[1].Code);
        Assert.Equal("Fire TV", recent[1].Client);
    }

    [Fact]
    public void GetRecent_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            var eventId = InsertPlaybackEvent($"session-{i}", "Fire TV", "Living Room");
            _diagnosisRepository.Insert(eventId, BuildDiagnosis($"CODE_{i}"));
        }

        var recent = _diagnosisRepository.GetRecent(2);

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
            Evidence = new[] { "fact one", "fact two" },
            Explanation = "explanation",
            Recommendation = "recommendation"
        };
        _diagnosisRepository.Insert(eventId, diagnosis);

        var recent = _diagnosisRepository.GetRecent(50);

        Assert.Equal(new[] { "fact one", "fact two" }, recent[0].Evidence);
    }

    private long InsertPlaybackEvent(string sessionId, string client, string deviceName)
    {
        var playbackEvent = new PlaybackEvent
        {
            SessionId = sessionId,
            ItemId = Guid.NewGuid().ToString(),
            Client = client,
            DeviceName = deviceName,
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            CreatedAtUtc = DateTime.UtcNow
        };

        return _playbackEventRepository.Insert(playbackEvent);
    }

    private static Diagnosis BuildDiagnosis(string code) => new()
    {
        Code = code,
        Confidence = Confidence.Confirmed,
        Evidence = new[] { "evidence" },
        Explanation = "explanation",
        Recommendation = "recommendation"
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
