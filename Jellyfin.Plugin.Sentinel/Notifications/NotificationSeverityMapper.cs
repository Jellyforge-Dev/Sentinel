using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// Maps a diagnosis's <see cref="Confidence"/> to the coarse severity vocabulary
/// (<c>"high"</c>/<c>"medium"</c>/<c>"low"</c>) used by <see cref="NotificationMessage.Severity"/>
/// and by each channel's configured minimum-severity threshold.
/// </summary>
public static class NotificationSeverityMapper
{
    /// <summary>Maps a confidence level to a severity string.</summary>
    /// <param name="confidence">The diagnosis's confidence.</param>
    /// <returns><c>"high"</c> for Confirmed/VeryLikely, <c>"medium"</c> for Likely, <c>"low"</c> for Possible/Unknown.</returns>
    public static string FromConfidence(Confidence confidence) => confidence switch
    {
        Confidence.Confirmed or Confidence.VeryLikely => "high",
        Confidence.Likely => "medium",
        _ => "low"
    };

    /// <summary>
    /// Gets whether <paramref name="severity"/> meets or exceeds <paramref name="minimumSeverity"/>,
    /// on the ordering low &lt; medium &lt; high. An unrecognized value in either argument is
    /// treated as <c>"low"</c> rather than throwing — config values are free-form strings from the
    /// dashboard, not a closed enum, so a typo or a future value this code doesn't know about must
    /// degrade safely rather than crash notification delivery.
    /// </summary>
    public static bool MeetsThreshold(string severity, string minimumSeverity) => Rank(severity) >= Rank(minimumSeverity);

    private static int Rank(string severity) => severity switch
    {
        "high" => 2,
        "medium" => 1,
        _ => 0
    };
}
