using System;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Notifications;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sentinel.Api;

/// <summary>
/// Read-only API serving Sentinel's collected diagnoses to the plugin's own configuration page.
/// This is not a general-purpose API — it exists only to back that one page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Sentinel")]
[Produces(MediaTypeNames.Application.Json)]
public class SentinelController : ControllerBase
{
    private readonly DiagnosisRepository _diagnosisRepository;
    private readonly ILibraryManager _libraryManager;
    private readonly LocalizationService _localizationService;
    private readonly IncidentRepository _incidentRepository;
    private readonly NotificationDispatcher _notificationDispatcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="SentinelController"/> class.
    /// </summary>
    /// <param name="diagnosisRepository">Repository for reading Sentinel's collected diagnoses.</param>
    /// <param name="libraryManager">Used to resolve a media item's display name from its id.</param>
    /// <param name="localizationService">Used to translate the dashboard's own UI strings.</param>
    /// <param name="incidentRepository">Repository for reading and updating Sentinel's tracked incidents.</param>
    /// <param name="notificationDispatcher">Used to send a test notification through a named channel.</param>
    public SentinelController(DiagnosisRepository diagnosisRepository, ILibraryManager libraryManager, LocalizationService localizationService, IncidentRepository incidentRepository, NotificationDispatcher notificationDispatcher)
    {
        ArgumentNullException.ThrowIfNull(diagnosisRepository);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(incidentRepository);
        ArgumentNullException.ThrowIfNull(notificationDispatcher);
        _diagnosisRepository = diagnosisRepository;
        _libraryManager = libraryManager;
        _localizationService = localizationService;
        _incidentRepository = incidentRepository;
        _notificationDispatcher = notificationDispatcher;
    }

    /// <summary>
    /// Gets the most recent diagnoses Sentinel has produced, most recent first.
    /// </summary>
    /// <returns>The recent diagnoses, with the originating media item's display name resolved.</returns>
    [HttpGet("diagnoses")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetRecentDiagnoses()
    {
        var language = Plugin.Instance?.Configuration.Language ?? Localization.SupportedLanguage.En;
        var summaries = _diagnosisRepository.GetRecent(50, language);

        var response = summaries.Select(summary =>
        {
            var knownIssue = KnownCoreIssues.Match(summary.Code);

            return new
            {
                summary.Id,
                summary.Code,
                Confidence = summary.Confidence.ToString(),
                summary.Evidence,
                summary.Explanation,
                summary.Recommendation,
                summary.CreatedAtUtc,
                summary.Client,
                summary.DeviceName,
                summary.UserName,
                summary.PlayMethod,
                MediaName = ResolveMediaName(summary.ItemId),
                KnownIssueUrl = knownIssue?.IssueUrl,
                KnownIssueExplanation = knownIssue?.Explanation
            };
        });

        return Ok(response);
    }

    /// <summary>
    /// Gets the most recently updated incidents, most recent first, each with the translated
    /// explanation/recommendation from its most recently linked diagnosis.
    /// </summary>
    /// <returns>The recent incidents, with the originating media item's display name resolved.</returns>
    [HttpGet("incidents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetIncidents()
    {
        var language = Plugin.Instance?.Configuration.Language ?? Localization.SupportedLanguage.En;
        var incidents = _incidentRepository.GetRecent(50);

        var response = incidents.Select(incident =>
        {
            var latestDiagnosisId = _incidentRepository.GetLatestDiagnosisId(incident.Id);
            var diagnosis = latestDiagnosisId is long diagnosisId ? _diagnosisRepository.GetById(diagnosisId, language) : null;
            var knownIssue = KnownCoreIssues.Match(incident.Code);

            return new
            {
                incident.Id,
                incident.Code,
                Status = incident.Status.ToString(),
                incident.OccurrenceCount,
                incident.FirstSeenUtc,
                incident.LastSeenUtc,
                incident.AcknowledgedAtUtc,
                incident.ResolvedAtUtc,
                incident.Client,
                incident.DeviceName,
                incident.UserName,
                incident.ResolutionNote,
                incident.IsExcepted,
                MediaName = ResolveMediaName(incident.ItemId),
                Explanation = diagnosis?.Explanation,
                Recommendation = diagnosis?.Recommendation,
                Evidence = diagnosis?.Evidence,
                Confidence = diagnosis?.Confidence.ToString(),
                KnownIssueUrl = knownIssue?.IssueUrl,
                KnownIssueExplanation = knownIssue?.Explanation
            };
        });

        return Ok(response);
    }

    /// <summary>
    /// Marks an incident as acknowledged.
    /// </summary>
    /// <param name="id">The incident's ID.</param>
    [HttpPost("incidents/{id}/acknowledge")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult AcknowledgeIncident(long id)
    {
        _incidentRepository.Acknowledge(id);
        return Ok();
    }

    /// <summary>
    /// Marks an incident as resolved.
    /// </summary>
    /// <param name="id">The incident's ID.</param>
    [HttpPost("incidents/{id}/resolve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResolveIncident(long id)
    {
        _incidentRepository.Resolve(id);
        return Ok();
    }

    /// <summary>
    /// Sets an incident's resolution note — free text on what ultimately fixed the underlying
    /// problem for the affected user, e.g. "enabled hardware transcoding on this device".
    /// </summary>
    /// <param name="id">The incident's ID.</param>
    /// <param name="request">The note text.</param>
    [HttpPost("incidents/{id}/note")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult UpdateIncidentNote(long id, [FromBody] IncidentNoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _incidentRepository.UpdateResolutionNote(id, request.Note ?? string.Empty);
        return Ok();
    }

    /// <summary>
    /// Marks this incident's user as an accepted exception — e.g. a user who intentionally always
    /// transcodes via a GPU — so every incident for that user (any rule, any media item) stops
    /// being surfaced as a problem needing attention, and future notifications for that user are
    /// suppressed.
    /// </summary>
    /// <param name="id">The incident's ID.</param>
    [HttpPost("incidents/{id}/except")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ExceptIncident(long id)
    {
        var incident = _incidentRepository.GetRecent(500).FirstOrDefault(i => i.Id == id);
        if (incident is null)
        {
            return NotFound();
        }

        _incidentRepository.MarkExcepted(incident.UserName, string.Empty);
        return Ok();
    }

    /// <summary>
    /// Removes the exception on this incident's user, so that user's incidents are treated as
    /// normal problems again.
    /// </summary>
    /// <param name="id">The incident's ID.</param>
    [HttpPost("incidents/{id}/unexcept")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult UnexceptIncident(long id)
    {
        var incident = _incidentRepository.GetRecent(500).FirstOrDefault(i => i.Id == id);
        if (incident is null)
        {
            return NotFound();
        }

        _incidentRepository.ClearExcepted(incident.UserName);
        return Ok();
    }

    /// <summary>
    /// Gets the current language's dashboard UI strings, for the plugin's own configuration page
    /// to render its static labels in.
    /// </summary>
    /// <returns>A flat object of UI_* translation keys to translated text.</returns>
    [HttpGet("ui-strings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetUiStrings()
    {
        var language = Plugin.Instance?.Configuration.Language ?? Localization.SupportedLanguage.En;
        var keys = new[]
        {
            "UI_TITLE", "UI_INTRO", "UI_COL_TIME", "UI_COL_MEDIA", "UI_COL_CLIENT_DEVICE",
            "UI_COL_CONFIDENCE", "UI_COL_EXPLANATION", "UI_LOADING", "UI_NO_DIAGNOSES",
            "UI_LOAD_FAILED", "UI_EVIDENCE_LABEL", "UI_RECOMMENDATION_LABEL", "UI_CODE_LABEL",
            "UI_KNOWN_ISSUE_LABEL",
            "UI_COL_STATUS", "UI_COL_OCCURRENCES", "UI_COL_FIRST_SEEN", "UI_COL_LAST_SEEN",
            "UI_ACKNOWLEDGE_BUTTON", "UI_RESOLVE_BUTTON",
            "UI_STATUS_DETECTED", "UI_STATUS_ACKNOWLEDGED", "UI_STATUS_RESOLVED", "UI_STATUS_REOPENED",
            "UI_NO_INCIDENTS", "UI_LOAD_INCIDENTS_FAILED",
            "UI_COL_USER", "UI_STATS_TOTAL", "UI_STATS_ACTIVE", "UI_FILTER_ALL", "UI_NO_INCIDENTS_FOR_FILTER",
            "UI_CLICK_FOR_DETAILS", "UI_NOTE_LABEL", "UI_NOTE_PLACEHOLDER", "UI_NOTE_SAVE_BUTTON", "UI_NOTE_SAVED",
            "UI_MARK_EXCEPTION_BUTTON", "UI_UNMARK_EXCEPTION_BUTTON", "UI_FILTER_EXPECTED", "UI_EXCEPTED_BADGE",
            "UI_LANGUAGE_LABEL", "UI_NOTIFICATION_CHANNELS_HEADER", "UI_SAVE_BUTTON", "UI_ENABLE_LABEL",
            "UI_MIN_SEVERITY_LABEL", "UI_SEND_TEST_BUTTON", "UI_SEVERITY_LOW", "UI_SEVERITY_MEDIUM", "UI_SEVERITY_HIGH",
            "UI_WEBHOOK_URL_LABEL", "UI_DISCORD_WEBHOOK_URL_LABEL", "UI_TELEGRAM_BOT_TOKEN_LABEL", "UI_TELEGRAM_CHAT_ID_LABEL",
            "UI_SMTP_HOST_LABEL", "UI_SMTP_PORT_LABEL", "UI_SMTP_USERNAME_LABEL", "UI_SMTP_PASSWORD_LABEL",
            "UI_EMAIL_FROM_LABEL", "UI_EMAIL_TO_LABEL",
            "UI_TEST_SAVING", "UI_TEST_SENDING", "UI_TEST_SUCCESS", "UI_TEST_FAILURE_GENERIC", "UI_TEST_FAILURE_PREFIX"
        };

        var result = keys.ToDictionary(key => key, key => _localizationService.Translate(key, language));
        return Ok(result);
    }

    /// <summary>
    /// Sends a fixed test message through the named notification channel, for the dashboard's
    /// per-channel "Send test" button.
    /// </summary>
    /// <param name="channelName">The channel to test (webhook/discord/telegram/email).</param>
    /// <param name="cancellationToken">Used to cancel the send.</param>
    [HttpPost("notifications/test/{channelName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> SendTestNotification(string channelName, CancellationToken cancellationToken)
    {
        var result = await _notificationDispatcher.SendTestNotificationAsync(channelName, cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok() : StatusCode(StatusCodes.Status502BadGateway, new { result.Reason });
    }

    /// <summary>
    /// Resolves a media item's display name from its id, falling back to the raw id if the item
    /// can no longer be found (e.g. it was deleted from the library since the diagnosis was made).
    /// For TV episodes, prefixes the series name for context (e.g., "Breaking Bad - Pilot").
    /// </summary>
    private string ResolveMediaName(string itemId)
    {
        if (!Guid.TryParse(itemId, out var guid))
        {
            return itemId;
        }

        var item = _libraryManager.GetItemById(guid);
        if (item is null)
        {
            return itemId;
        }

        if (item is MediaBrowser.Controller.Entities.IHasSeries hasSeries)
        {
            var seriesName = hasSeries.FindSeriesName();
            return string.IsNullOrEmpty(seriesName) ? item.Name : $"{seriesName} - {item.Name}";
        }

        return item.Name;
    }
}

/// <summary>
/// The request body for <see cref="SentinelController.UpdateIncidentNote"/>.
/// </summary>
public sealed class IncidentNoteRequest
{
    /// <summary>Gets or sets the note text.</summary>
    public string? Note { get; set; }
}
