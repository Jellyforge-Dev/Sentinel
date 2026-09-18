using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Sentinel.Collector;

/// <summary>
/// The play-method and transcode-related fields captured from a <c>PlaybackProgress</c> event,
/// cached per session so <see cref="PlaybackCollectorHostedService"/> can still use them once
/// <c>PlaybackStopped</c> fires.
/// </summary>
/// <remarks>
/// This exists because of a verified Jellyfin behavior (confirmed against
/// <c>Emby.Server.Implementations.Session.SessionManager</c> source, not guessed): before firing
/// <c>PlaybackStopped</c>, Jellyfin's own <c>OnPlaybackStopped</c> calls
/// <c>RemoveNowPlayingItem(session)</c>, which unconditionally resets
/// <c>session.PlayState</c> to a brand-new, empty <c>PlayerStateInfo</c> and clears
/// <c>session.TranscodingInfo</c> to null — for every session, regardless of whether it was
/// transcoding. By the time a <c>PlaybackStopped</c> handler runs, <c>Session.PlayState.PlayMethod</c>
/// and <c>Session.TranscodingInfo</c> are therefore always empty; they must be captured earlier,
/// during <c>PlaybackProgress</c>, where they are still live.
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
}
