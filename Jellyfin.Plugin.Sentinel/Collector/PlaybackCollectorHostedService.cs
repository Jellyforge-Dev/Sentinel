using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Collector;

/// <summary>
/// Observes finished playback sessions and turns them into persisted diagnoses.
/// Registered as an <see cref="IHostedService"/> — the main <see cref="Plugin"/> class
/// cannot be one itself, per Jellyfin's plugin constraints.
/// </summary>
/// <remarks>
/// Also subscribes to <c>PlaybackProgress</c>, not just <c>PlaybackStopped</c>. This is required,
/// not optional: verified against <c>Emby.Server.Implementations.Session.SessionManager</c>
/// source, Jellyfin's own <c>OnPlaybackStopped</c> always calls <c>RemoveNowPlayingItem(session)</c>
/// — which resets <c>session.PlayState</c> to a brand-new, empty object and clears
/// <c>session.TranscodingInfo</c> to null — before firing <c>PlaybackStopped</c>. A handler that
/// only listens to <c>PlaybackStopped</c> and reads <c>Session.PlayState</c>/<c>TranscodingInfo</c>
/// will therefore always see empty values, for every session, regardless of whether it actually
/// transcoded. The last known state must be captured earlier, during <c>PlaybackProgress</c>
/// (where it is still live), cached per session, and consumed when the session stops.
/// </remarks>
public sealed partial class PlaybackCollectorHostedService : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly RuleEngine _ruleEngine;
    private readonly PlaybackEventRepository _playbackEventRepository;
    private readonly DiagnosisRepository _diagnosisRepository;
    private readonly ILogger<PlaybackCollectorHostedService> _logger;
    private readonly ConcurrentDictionary<string, PlaybackProgressSnapshot> _lastKnownPlaybackState = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCollectorHostedService"/> class.
    /// </summary>
    /// <param name="sessionManager">The Jellyfin session manager to observe playback events from.</param>
    /// <param name="ruleEngine">The rule engine used to diagnose playback events.</param>
    /// <param name="playbackEventRepository">The repository used to persist playback events.</param>
    /// <param name="diagnosisRepository">The repository used to persist diagnoses.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public PlaybackCollectorHostedService(
        ISessionManager sessionManager,
        RuleEngine ruleEngine,
        PlaybackEventRepository playbackEventRepository,
        DiagnosisRepository diagnosisRepository,
        ILogger<PlaybackCollectorHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(ruleEngine);
        ArgumentNullException.ThrowIfNull(playbackEventRepository);
        ArgumentNullException.ThrowIfNull(diagnosisRepository);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionManager = sessionManager;
        _ruleEngine = ruleEngine;
        _playbackEventRepository = playbackEventRepository;
        _diagnosisRepository = diagnosisRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        LogCollectorStarted(_logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs args)
    {
        // Same never-throw contract as OnPlaybackStopped below — this runs on Jellyfin's own
        // event-invocation thread too.
        try
        {
            var session = args.Session;
            var sessionId = session?.Id;
            if (session is null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            var transcodingInfo = session.TranscodingInfo;

            // Overwritten on every progress tick so this always holds the most recent state
            // observed before the session eventually stops.
            _lastKnownPlaybackState[sessionId] = new PlaybackProgressSnapshot
            {
                PlayMethod = session.PlayState?.PlayMethod,
                TranscodeReasons = transcodingInfo?.TranscodeReasons ?? default,
                VideoCodec = transcodingInfo?.VideoCodec,
                AudioCodec = transcodingInfo?.AudioCodec
            };
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogPlaybackProgressHandlerFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        // This handler runs directly on Jellyfin's own event-invocation thread. An unhandled
        // exception here (e.g. a SqliteException from a full disk or a locked file) would
        // propagate back into Jellyfin's own playback-stop code path and could break Jellyfin's
        // own bookkeeping for the session (such as saving resume position). Never let anything
        // escape this handler.
        try
        {
            var sessionId = args.Session?.Id;
            PlaybackProgressSnapshot? snapshot = null;
            if (!string.IsNullOrEmpty(sessionId))
            {
                // Removed, not just read: this session is ending, and leaving stale entries
                // behind would grow this dictionary unbounded over the server's lifetime.
                _lastKnownPlaybackState.TryRemove(sessionId, out snapshot);
            }

            var playbackEvent = PlaybackEventFactory.FromEventArgs(args, snapshot);
            if (playbackEvent is null)
            {
                return;
            }

            var playbackEventId = _playbackEventRepository.Insert(playbackEvent);
            var diagnoses = _ruleEngine.Diagnose(playbackEvent);

            foreach (var diagnosis in diagnoses)
            {
                _diagnosisRepository.Insert(playbackEventId, diagnosis);
                LogDiagnosis(_logger, diagnosis.Code, diagnosis.Confidence, playbackEvent.SessionId);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogPlaybackStoppedHandlerFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sentinel diagnosis {Code} ({Confidence}) for session {SessionId}")]
    private static partial void LogDiagnosis(ILogger logger, string code, Confidence confidence, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sentinel playback collector started and subscribed to playback-progress and playback-stopped events")]
    private static partial void LogCollectorStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sentinel failed to process a playback-stopped event; Jellyfin's own playback handling was not affected")]
    private static partial void LogPlaybackStoppedHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sentinel failed to process a playback-progress event; Jellyfin's own playback handling was not affected")]
    private static partial void LogPlaybackProgressHandlerFailed(ILogger logger, Exception exception);
}
