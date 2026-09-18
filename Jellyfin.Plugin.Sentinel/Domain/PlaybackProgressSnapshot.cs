using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// The play-method and transcode-related fields captured from a <c>PlaybackProgress</c> event,
/// cached per play session so <c>PlaybackCollectorHostedService</c> can still use them once
/// <c>PlaybackStopped</c> fires.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a verified Jellyfin behavior (confirmed against
/// <c>Emby.Server.Implementations.Session.SessionManager</c> source, not guessed): before firing
/// <c>PlaybackStopped</c>, Jellyfin's own <c>OnPlaybackStopped</c> calls
/// <c>RemoveNowPlayingItem(session)</c>, which unconditionally resets
/// <c>session.PlayState</c> to a brand-new, empty <c>PlayerStateInfo</c> and clears
/// <c>session.TranscodingInfo</c> to null — for every session, regardless of whether it was
/// transcoding. By the time a <c>PlaybackStopped</c> handler runs, <c>Session.PlayState.PlayMethod</c>
/// and <c>Session.TranscodingInfo</c> are therefore always empty; they must be captured earlier,
/// during <c>PlaybackProgress</c>, where they are still live.
/// </para>
/// <para>
/// Cached primarily by <c>PlaySessionId</c> — a value unique to one playback — not
/// <c>Session.Id</c>. <c>Session.Id</c> is derived from app name + device ID + user ID
/// (<c>SessionManager.GetSessionKey</c>, verified against real source) and stays identical
/// across every playback the same client/device ever does. Keying only by it would let a
/// leftover snapshot from an earlier playback get attributed to a later, unrelated one on the
/// same device.
/// </para>
/// </remarks>
public sealed class PlaybackProgressSnapshot
{
    /// <summary>
    /// Gets the play method observed during playback, before Jellyfin reset it at stop time.
    /// </summary>
    public required PlayMethod? PlayMethod { get; init; }

    /// <summary>
    /// Gets the transcode reasons observed during playback, before Jellyfin cleared them at stop time.
    /// </summary>
    public required TranscodeReason TranscodeReasons { get; init; }

    /// <summary>
    /// Gets the video codec observed during playback, if it was transcoding.
    /// </summary>
    public string? VideoCodec { get; init; }

    /// <summary>
    /// Gets the audio codec observed during playback, if it was transcoding.
    /// </summary>
    public string? AudioCodec { get; init; }

    /// <summary>
    /// Gets an opaque monotonic timestamp (from <see cref="System.TimeProvider.GetTimestamp"/>)
    /// recorded when this snapshot was captured.
    /// </summary>
    /// <remarks>
    /// Used to evict entries for play sessions that never fire <c>PlaybackStopped</c> (a client
    /// crash, a network drop, a server restart mid-playback). Because the cache is keyed by
    /// <c>PlaySessionId</c> rather than the device-scoped <c>Session.Id</c>, its keyspace is not
    /// naturally bounded by the number of devices a server has ever seen — without eviction it
    /// would grow for as long as the server runs. Deliberately a monotonic timestamp rather than
    /// <see cref="System.DateTime.UtcNow"/>: a system-clock adjustment (an NTP correction after a
    /// server without a battery-backed clock loses power, for example) must not evict every
    /// in-progress snapshot at once, nor freeze eviction indefinitely.
    /// </remarks>
    public required long CapturedAtTimestamp { get; init; }
}
