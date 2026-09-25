using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// A single notification-worthy event, already translated and evidence-attached — the shape every
/// <see cref="INotificationChannel"/> sends, regardless of destination.
/// </summary>
[SuppressMessage("Design", "CA1056:Uri properties should not be strings")]
public sealed class NotificationMessage
{
    /// <summary>Gets the notification's title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the notification's body text.</summary>
    public required string Body { get; init; }

    /// <summary>Gets the severity level this notification maps to ("info"/"notice"/"warning"/"important"/"critical") — see <see cref="NotificationSeverityMapper"/> for how this is derived from a diagnosis's confidence.</summary>
    public required string Severity { get; init; }

    /// <summary>Gets a deep link back to the dashboard for this incident, if one could be constructed.</summary>
    public required string IncidentUrl { get; init; }
}
