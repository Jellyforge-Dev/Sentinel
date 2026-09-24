using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// One outbound notification destination (webhook, Discord, Telegram, email, ...). Implementations
/// must never throw out of <see cref="SendAsync"/> — a failed send is reported via the boolean
/// return value and logged internally, never propagated, since a notification-delivery failure must
/// never be allowed to break the Incident Engine that triggers it.
/// </summary>
public interface INotificationChannel
{
    /// <summary>Gets this channel's display name, for logging and the config UI's per-channel test-send button.</summary>
    string ChannelName { get; }

    /// <summary>
    /// Gets a short, human-readable description of why the most recent <see cref="SendAsync"/>
    /// call returned false — an HTTP status code or the underlying exception's message — or null if
    /// the most recent call succeeded or none has been made yet. Exists so the dashboard's "Send
    /// test" button can show an admin something more actionable than "it failed"; never used by the
    /// production dispatch path, which only logs (see each implementation's own Warning-level log).
    /// </summary>
    string? LastFailureReason { get; }

    /// <summary>Sends a notification through this channel.</summary>
    /// <param name="message">The notification to send.</param>
    /// <param name="cancellationToken">Used to cancel the send.</param>
    /// <returns>True if the send succeeded; false if it failed for any reason (never throws).</returns>
    Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
