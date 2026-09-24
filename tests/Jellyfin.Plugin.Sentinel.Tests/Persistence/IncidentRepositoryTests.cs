using System;
using System.IO;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Persistence;

public class IncidentRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;
    private readonly PlaybackEventRepository _playbackEventRepository;
    private readonly DiagnosisRepository _diagnosisRepository;
    private readonly IncidentRepository _incidentRepository;
    private readonly LocalizationService _localizationService = new();

    public IncidentRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-incident-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
        _playbackEventRepository = new PlaybackEventRepository(_database);
        _diagnosisRepository = new DiagnosisRepository(_database, _localizationService, NullLogger<DiagnosisRepository>.Instance);
        _incidentRepository = new IncidentRepository(_database);
    }

    [Fact]
    public void UpsertOnDiagnosis_CreatesNewIncident_WhenNoneExistsForFingerprint()
    {
        var itemId = Guid.NewGuid().ToString();
        var diagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);

        var result = _incidentRepository.UpsertOnDiagnosis(diagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false);

        Assert.True(result.IsNewOrReopened);

        var recent = _incidentRepository.GetRecent(50);

        Assert.Single(recent);
        var incident = recent[0];
        Assert.Equal(IncidentStatus.Detected, incident.Status);
        Assert.Equal(1, incident.OccurrenceCount);
        Assert.Equal("BUFFERING", incident.Code);
        Assert.Equal(itemId, incident.ItemId);
        Assert.Equal("Fire TV", incident.Client);
        Assert.Equal("Living Room", incident.DeviceName);
        Assert.Equal("Alice", incident.UserName);
        Assert.Null(incident.AcknowledgedAtUtc);
        Assert.Null(incident.ResolvedAtUtc);
    }

    [Fact]
    public void UpsertOnDiagnosis_IncrementsExistingIncident_WhenSameFingerprintRecurs()
    {
        var itemId = Guid.NewGuid().ToString();

        var firstDiagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);
        _incidentRepository.UpsertOnDiagnosis(firstDiagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false);

        var secondDiagnosisId = InsertDiagnosis("BUFFERING", "session-b", "Fire TV", "Living Room", itemId);
        var result = _incidentRepository.UpsertOnDiagnosis(secondDiagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false);

        Assert.False(result.IsNewOrReopened);

        var recent = _incidentRepository.GetRecent(50);

        Assert.Single(recent);
        Assert.Equal(2, recent[0].OccurrenceCount);
        Assert.Equal(IncidentStatus.Detected, recent[0].Status);
    }

    [Fact]
    public void UpsertOnDiagnosis_UpdatesUserName_ToTheMostRecentOccurrences_User()
    {
        var itemId = Guid.NewGuid().ToString();

        var firstDiagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);
        _incidentRepository.UpsertOnDiagnosis(firstDiagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false);

        var secondDiagnosisId = InsertDiagnosis("BUFFERING", "session-b", "Fire TV", "Living Room", itemId);
        _incidentRepository.UpsertOnDiagnosis(secondDiagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Bob", false);

        var recent = _incidentRepository.GetRecent(50);

        Assert.Single(recent);
        Assert.Equal("Bob", recent[0].UserName);
    }

    [Fact]
    public void UpsertOnDiagnosis_ReopensIncident_WhenFingerprintRecursAfterResolution()
    {
        var itemId = Guid.NewGuid().ToString();

        var firstDiagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);
        var incidentId = _incidentRepository.UpsertOnDiagnosis(firstDiagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false).IncidentId;

        _incidentRepository.Resolve(incidentId);

        var secondDiagnosisId = InsertDiagnosis("BUFFERING", "session-b", "Fire TV", "Living Room", itemId);
        var result = _incidentRepository.UpsertOnDiagnosis(secondDiagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false);

        Assert.True(result.IsNewOrReopened);

        var recent = _incidentRepository.GetRecent(50);

        Assert.Single(recent);
        var incident = recent[0];
        Assert.Equal(incidentId, incident.Id);
        Assert.Equal(IncidentStatus.Reopened, incident.Status);
        Assert.Equal(2, incident.OccurrenceCount);
        Assert.Null(incident.ResolvedAtUtc);
    }

    [Fact]
    public void Acknowledge_SetsStatusAndTimestamp()
    {
        var itemId = Guid.NewGuid().ToString();
        var diagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);
        var incidentId = _incidentRepository.UpsertOnDiagnosis(diagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false).IncidentId;

        _incidentRepository.Acknowledge(incidentId);

        var recent = _incidentRepository.GetRecent(50);

        Assert.Equal(IncidentStatus.Acknowledged, recent[0].Status);
        Assert.NotNull(recent[0].AcknowledgedAtUtc);
    }

    [Fact]
    public void Resolve_SetsStatusAndTimestamp()
    {
        var itemId = Guid.NewGuid().ToString();
        var diagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);
        var incidentId = _incidentRepository.UpsertOnDiagnosis(diagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false).IncidentId;

        _incidentRepository.Resolve(incidentId);

        var recent = _incidentRepository.GetRecent(50);

        Assert.Equal(IncidentStatus.Resolved, recent[0].Status);
        Assert.NotNull(recent[0].ResolvedAtUtc);
    }

    [Fact]
    public void UpdateResolutionNote_SetsNote_OnMatchingIncident()
    {
        var itemId = Guid.NewGuid().ToString();
        var diagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);
        var incidentId = _incidentRepository.UpsertOnDiagnosis(diagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false).IncidentId;

        _incidentRepository.UpdateResolutionNote(incidentId, "Enabled hardware transcoding for this device.");

        var recent = _incidentRepository.GetRecent(50);

        Assert.Equal("Enabled hardware transcoding for this device.", recent[0].ResolutionNote);
    }

    [Fact]
    public void IsExcepted_ReturnsFalse_WhenNoMatchingExceptionExists()
    {
        Assert.False(_incidentRepository.IsExcepted("BUFFERING", "Alice"));
    }

    [Fact]
    public void MarkExcepted_MakesIsExceptedTrue_ForMatchingCodeAndUser_ButNotForOtherUsers()
    {
        Assert.False(_incidentRepository.IsExcepted("BUFFERING", "Alice"));

        _incidentRepository.MarkExcepted("BUFFERING", "Alice", "Uses a GPU for transcoding intentionally.");

        Assert.True(_incidentRepository.IsExcepted("BUFFERING", "Alice"));
        Assert.False(_incidentRepository.IsExcepted("BUFFERING", "Bob"));
    }

    [Fact]
    public void MarkExcepted_RetroactivelyMarksAlreadyOpenIncidents_AsExcepted()
    {
        var itemId = Guid.NewGuid().ToString();
        var diagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);
        _incidentRepository.UpsertOnDiagnosis(diagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false);

        _incidentRepository.MarkExcepted("BUFFERING", "Alice", string.Empty);

        var recent = _incidentRepository.GetRecent(50);

        Assert.True(recent[0].IsExcepted);
    }

    [Fact]
    public void UpsertOnDiagnosis_DoesNotMarkNewIncidentExcepted_WhenIsExceptedArgumentIsFalse()
    {
        var itemId = Guid.NewGuid().ToString();
        var diagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);

        _incidentRepository.UpsertOnDiagnosis(diagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false);

        Assert.False(_incidentRepository.GetRecent(50)[0].IsExcepted);
    }

    [Fact]
    public void ClearExcepted_MakesIsExceptedFalse_ForMatchingIncidentsAndFutureUpserts()
    {
        var itemId = Guid.NewGuid().ToString();
        var diagnosisId = InsertDiagnosis("BUFFERING", "session-a", "Fire TV", "Living Room", itemId);
        _incidentRepository.UpsertOnDiagnosis(diagnosisId, "BUFFERING", itemId, "Fire TV", "Living Room", "Alice", false);
        _incidentRepository.MarkExcepted("BUFFERING", "Alice", string.Empty);

        _incidentRepository.ClearExcepted("BUFFERING", "Alice");

        Assert.False(_incidentRepository.IsExcepted("BUFFERING", "Alice"));
        Assert.False(_incidentRepository.GetRecent(50)[0].IsExcepted);
    }

    private long InsertDiagnosis(string code, string sessionId, string client, string deviceName, string itemId)
    {
        var playbackEvent = new PlaybackEvent
        {
            SessionId = sessionId,
            ItemId = itemId,
            Client = client,
            DeviceName = deviceName,
            UserName = "Alice",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            CreatedAtUtc = DateTime.UtcNow
        };
        var playbackEventId = _playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = code,
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "evidence" }
        };

        return _diagnosisRepository.Insert(playbackEventId, diagnosis);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
