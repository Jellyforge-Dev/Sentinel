using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// Maps a diagnosis's <see cref="Confidence"/> 1:1 to the five-level severity vocabulary
/// (<c>"info"</c>/<c>"notice"</c>/<c>"warning"</c>/<c>"important"</c>/<c>"critical"</c>) used by
/// <see cref="NotificationMessage.Severity"/> and by each channel's configured minimum-severity
/// threshold. This exact five-level scheme — including the color coding — is the one already drawn
/// in Sentinel's own logo artwork (Info=blue, Notice=green, Warning=yellow, Important=orange,
/// Critical=red); this mapper makes the running app match what the branding already promised
/// instead of the three-level low/medium/high scheme used before v0.1.6.3.
/// </summary>
public static class NotificationSeverityMapper
{
    /// <summary>Maps a confidence level to a severity string.</summary>
    /// <param name="confidence">The diagnosis's confidence.</param>
    /// <returns>The matching severity: Confirmed→"critical", VeryLikely→"important", Likely→"warning", Possible→"notice", Unknown→"info".</returns>
    public static string FromConfidence(Confidence confidence) => confidence switch
    {
        Confidence.Confirmed => "critical",
        Confidence.VeryLikely => "important",
        Confidence.Likely => "warning",
        Confidence.Possible => "notice",
        _ => "info"
    };

    /// <summary>
    /// Gets whether <paramref name="severity"/> meets or exceeds <paramref name="minimumSeverity"/>,
    /// on the ordering info &lt; notice &lt; warning &lt; important &lt; critical. An unrecognized
    /// value in either argument is treated as <c>"info"</c> (the lowest rank) rather than throwing —
    /// config values are free-form strings from the dashboard, not a closed enum, so a typo or a
    /// future value this code doesn't know about must degrade safely rather than crash notification
    /// delivery.
    /// </summary>
    public static bool MeetsThreshold(string severity, string minimumSeverity) => Rank(severity) >= Rank(minimumSeverity);

    private static int Rank(string severity) => severity switch
    {
        "critical" => 4,
        "important" => 3,
        "warning" => 2,
        "notice" => 1,
        _ => 0
    };
}
