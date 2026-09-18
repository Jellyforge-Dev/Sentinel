using System;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.Sentinel.Diagnostics;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SentinelController"/> class.
    /// </summary>
    /// <param name="diagnosisRepository">Repository for reading Sentinel's collected diagnoses.</param>
    /// <param name="libraryManager">Used to resolve a media item's display name from its id.</param>
    public SentinelController(DiagnosisRepository diagnosisRepository, ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(diagnosisRepository);
        ArgumentNullException.ThrowIfNull(libraryManager);
        _diagnosisRepository = diagnosisRepository;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets the most recent diagnoses Sentinel has produced, most recent first.
    /// </summary>
    /// <returns>The recent diagnoses, with the originating media item's display name resolved.</returns>
    [HttpGet("diagnoses")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetRecentDiagnoses()
    {
        var summaries = _diagnosisRepository.GetRecent(50);

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
                summary.PlayMethod,
                MediaName = ResolveMediaName(summary.ItemId),
                KnownIssueUrl = knownIssue?.IssueUrl,
                KnownIssueExplanation = knownIssue?.Explanation
            };
        });

        return Ok(response);
    }

    /// <summary>
    /// Resolves a media item's display name from its id, falling back to the raw id if the item
    /// can no longer be found (e.g. it was deleted from the library since the diagnosis was made).
    /// </summary>
    private string ResolveMediaName(string itemId)
    {
        if (!Guid.TryParse(itemId, out var guid))
        {
            return itemId;
        }

        return _libraryManager.GetItemById(guid)?.Name ?? itemId;
    }
}
