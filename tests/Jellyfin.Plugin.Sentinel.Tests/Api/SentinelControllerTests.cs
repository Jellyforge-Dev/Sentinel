using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Sentinel.Api;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Api;

public class SentinelControllerTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;
    private readonly PlaybackEventRepository _playbackEventRepository;
    private readonly DiagnosisRepository _diagnosisRepository;
    private readonly IncidentRepository _incidentRepository;
    private readonly LocalizationService _localizationService = new();
    private readonly SentinelController _controller;

    public SentinelControllerTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-controller-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
        _playbackEventRepository = new PlaybackEventRepository(_database);
        _diagnosisRepository = new DiagnosisRepository(_database, _localizationService, NullLogger<DiagnosisRepository>.Instance);
        _incidentRepository = new IncidentRepository(_database);

        var libraryManagerMock = new Mock<ILibraryManager>();
        _controller = new SentinelController(_diagnosisRepository, libraryManagerMock.Object, _localizationService, _incidentRepository);
    }

    [Fact]
    public void GetRecentDiagnoses_TranslatesExplanationAndRecommendation_ForARealRuleCode()
    {
        // Plugin.Instance is null in a test process, so the controller falls back to
        // SupportedLanguage.En — exactly the path this test exercises, no Plugin mocking needed.
        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-1",
            ItemId = Guid.NewGuid().ToString(),
            Client = "Fire TV",
            DeviceName = "Living Room",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            CreatedAtUtc = DateTime.UtcNow
        };
        var playbackEventId = _playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "TranscodeReasons = VideoCodecNotSupported" }
        };
        _diagnosisRepository.Insert(playbackEventId, diagnosis);

        var result = Assert.IsType<OkObjectResult>(_controller.GetRecentDiagnoses());
        var response = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToList();

        Assert.Single(response);
        var explanation = response[0].GetType().GetProperty("Explanation")!.GetValue(response[0]);
        Assert.Equal(
            "Your device can't play this video's codec natively, so Jellyfin had to convert it on the fly.",
            explanation);
    }

    [Fact]
    public void GetUiStrings_ReturnsTranslatedUiTitle()
    {
        var result = Assert.IsType<OkObjectResult>(_controller.GetUiStrings());
        var dictionary = Assert.IsAssignableFrom<IDictionary<string, string>>(result.Value);

        Assert.Equal("Jellyfin Sentinel", dictionary["UI_TITLE"]);
    }

    [Fact]
    public void GetRecentDiagnoses_ShowsSeriesNameForEpisodes()
    {
        var episodeId = Guid.NewGuid();
        var episode = new Episode
        {
            Id = episodeId,
            Name = "Pilot",
            SeriesName = "Breaking Bad"
        };

        var libraryManagerMock = new Mock<ILibraryManager>();
        libraryManagerMock.Setup(x => x.GetItemById(episodeId)).Returns(episode);
        var controller = new SentinelController(_diagnosisRepository, libraryManagerMock.Object, _localizationService, _incidentRepository);

        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-2",
            ItemId = episodeId.ToString(),
            Client = "Web",
            DeviceName = "Browser",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.DirectStream,
            CreatedAtUtc = DateTime.UtcNow
        };
        var playbackEventId = _playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "Test evidence" }
        };
        _diagnosisRepository.Insert(playbackEventId, diagnosis);

        var result = Assert.IsType<OkObjectResult>(controller.GetRecentDiagnoses());
        var response = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToList();

        Assert.Single(response);
        var mediaName = response[0].GetType().GetProperty("MediaName")!.GetValue(response[0]);
        Assert.Equal("Breaking Bad - Pilot", mediaName);
    }

    [Fact]
    public void GetRecentDiagnoses_ShowsMovieNameWithoutSeries()
    {
        var movieId = Guid.NewGuid();
        var movie = new Movie
        {
            Id = movieId,
            Name = "Bumblebee"
        };

        var libraryManagerMock = new Mock<ILibraryManager>();
        libraryManagerMock.Setup(x => x.GetItemById(movieId)).Returns(movie);
        var controller = new SentinelController(_diagnosisRepository, libraryManagerMock.Object, _localizationService, _incidentRepository);

        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-3",
            ItemId = movieId.ToString(),
            Client = "Roku",
            DeviceName = "TV",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.DirectPlay,
            CreatedAtUtc = DateTime.UtcNow
        };
        var playbackEventId = _playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "Test evidence" }
        };
        _diagnosisRepository.Insert(playbackEventId, diagnosis);

        var result = Assert.IsType<OkObjectResult>(controller.GetRecentDiagnoses());
        var response = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToList();

        Assert.Single(response);
        var mediaName = response[0].GetType().GetProperty("MediaName")!.GetValue(response[0]);
        Assert.Equal("Bumblebee", mediaName);
    }

    [Fact]
    public void GetIncidents_ReturnsTranslatedExplanationFromLatestLinkedDiagnosis()
    {
        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-4",
            ItemId = Guid.NewGuid().ToString(),
            Client = "Fire TV",
            DeviceName = "Living Room",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            CreatedAtUtc = DateTime.UtcNow
        };
        var playbackEventId = _playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "TranscodeReasons = VideoCodecNotSupported" }
        };
        var diagnosisId = _diagnosisRepository.Insert(playbackEventId, diagnosis);
        _incidentRepository.UpsertOnDiagnosis(diagnosisId, diagnosis.Code, playbackEvent.ItemId, playbackEvent.Client, playbackEvent.DeviceName);

        var result = Assert.IsType<OkObjectResult>(_controller.GetIncidents());
        var response = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToList();

        Assert.Single(response);
        var explanation = response[0].GetType().GetProperty("Explanation")!.GetValue(response[0]);
        Assert.Equal(
            "Your device can't play this video's codec natively, so Jellyfin had to convert it on the fly.",
            explanation);
        var occurrenceCount = response[0].GetType().GetProperty("OccurrenceCount")!.GetValue(response[0]);
        Assert.Equal(1, occurrenceCount);
        var status = response[0].GetType().GetProperty("Status")!.GetValue(response[0]);
        Assert.Equal("Detected", status);
    }

    [Fact]
    public void AcknowledgeIncident_SetsStatusToAcknowledged()
    {
        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-5",
            ItemId = Guid.NewGuid().ToString(),
            Client = "Fire TV",
            DeviceName = "Living Room",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            CreatedAtUtc = DateTime.UtcNow
        };
        var playbackEventId = _playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "TranscodeReasons = VideoCodecNotSupported" }
        };
        var diagnosisId = _diagnosisRepository.Insert(playbackEventId, diagnosis);
        var incidentId = _incidentRepository.UpsertOnDiagnosis(diagnosisId, diagnosis.Code, playbackEvent.ItemId, playbackEvent.Client, playbackEvent.DeviceName);

        Assert.IsType<OkResult>(_controller.AcknowledgeIncident(incidentId));

        var result = Assert.IsType<OkObjectResult>(_controller.GetIncidents());
        var response = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToList();

        Assert.Single(response);
        var status = response[0].GetType().GetProperty("Status")!.GetValue(response[0]);
        Assert.Equal("Acknowledged", status);
    }

    [Fact]
    public void ResolveIncident_SetsStatusToResolved()
    {
        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-6",
            ItemId = Guid.NewGuid().ToString(),
            Client = "Fire TV",
            DeviceName = "Living Room",
            PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode,
            TranscodeReasons = MediaBrowser.Model.Session.TranscodeReason.VideoCodecNotSupported,
            CreatedAtUtc = DateTime.UtcNow
        };
        var playbackEventId = _playbackEventRepository.Insert(playbackEvent);

        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "TranscodeReasons = VideoCodecNotSupported" }
        };
        var diagnosisId = _diagnosisRepository.Insert(playbackEventId, diagnosis);
        var incidentId = _incidentRepository.UpsertOnDiagnosis(diagnosisId, diagnosis.Code, playbackEvent.ItemId, playbackEvent.Client, playbackEvent.DeviceName);

        Assert.IsType<OkResult>(_controller.ResolveIncident(incidentId));

        var result = Assert.IsType<OkObjectResult>(_controller.GetIncidents());
        var response = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToList();

        Assert.Single(response);
        var status = response[0].GetType().GetProperty("Status")!.GetValue(response[0]);
        Assert.Equal("Resolved", status);
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
