using System;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// A normalized record of one finished playback session, independent of
/// Jellyfin's own session/event types.
/// </summary>
public sealed class PlaybackEvent
{
    /// <summary>Gets the Jellyfin session ID this event was captured from.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the ID of the media item that was played.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the Jellyfin client name (e.g. "Fire TV").</summary>
    public required string Client { get; init; }

    /// <summary>Gets the device name reported by the client.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Gets the Jellyfin username for the session, if one was reported.</summary>
    public required string UserName { get; init; }

    /// <summary>Gets how playback was delivered (direct play, direct stream, or transcode).</summary>
    public PlayMethod? PlayMethod { get; init; }

    /// <summary>Gets the flags describing why Jellyfin transcoded this playback, if it did.</summary>
    public TranscodeReason TranscodeReasons { get; init; }

    /// <summary>Gets the source video codec, if known.</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Gets the source audio codec, if known.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Gets the active subtitle stream's format, if known.</summary>
    public string? SubtitleFormat { get; init; }

    /// <summary>Gets the UTC time this event was created.</summary>
    public required DateTime CreatedAtUtc { get; init; }
}
