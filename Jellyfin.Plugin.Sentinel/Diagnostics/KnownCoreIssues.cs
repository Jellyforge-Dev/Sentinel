using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// One verified, currently-open Jellyfin core issue that explains a confusing symptom.
/// This is a curated, hand-maintained table, not a live GitHub API poll — a stale entry
/// pointing at an already-fixed issue is a low-severity, easily corrected failure mode,
/// which is preferable to taking on a live external dependency for this.
/// </summary>
[SuppressMessage("Design", "CA1054:Uri parameters should not be strings")]
[SuppressMessage("Design", "CA1056:Uri properties should not be strings")]
public sealed record KnownCoreIssue(string Symptom, string IssueUrl, string Explanation);

/// <summary>
/// A curated lookup table mapping Sentinel diagnosis codes to known, verified, currently-open Jellyfin core GitHub issues.
/// </summary>
public static class KnownCoreIssues
{
    /// <summary>
    /// Gets the list of all known core issues.
    /// </summary>
    public static IReadOnlyList<KnownCoreIssue> All { get; } = new List<KnownCoreIssue>
    {
        new(
            Symptom: "TRANSCODE_REASON_MISSING",
            IssueUrl: "https://github.com/jellyfin/jellyfin/issues/12193",
            Explanation: "Jellyfin sometimes drops the transcode-reason field from its own logs. This is a known upstream bug, not a configuration problem on your server."),
    };

    /// <summary>
    /// Finds a known core issue matching a Sentinel diagnosis code, if any exists.
    /// </summary>
    /// <param name="diagnosisCode">The diagnosis code to look up.</param>
    /// <returns>A matching <see cref="KnownCoreIssue"/> if found; otherwise <c>null</c>.</returns>
    public static KnownCoreIssue? Match(string diagnosisCode) =>
        All.FirstOrDefault(issue => issue.Symptom == diagnosisCode);
}
