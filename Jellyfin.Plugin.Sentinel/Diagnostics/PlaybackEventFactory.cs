using System;
using Jellyfin.Plugin.Sentinel.Collector;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// Converts Jellyfin's own event args into Sentinel's normalized <see cref="PlaybackEvent"/>.
/// </summary>
public static class PlaybackEventFactory
{
    /// <summary>
    /// Builds a <see cref="PlaybackEvent"/> from a stopped-playback event, or null if the
    /// event doesn't carry enough information to diagnose (no session or no item).
    /// </summary>
    /// <param name="args">The stopped-playback event args from Jellyfin.</param>
    /// <param name="progressSnapshot">
    /// The most recent play-method/transcode-reason data captured from a <c>PlaybackProgress</c>
    /// event for this same session, or null if none was captured. Required because Jellyfin
    /// always clears <c>Session.PlayState</c> and <c>Session.TranscodingInfo</c> before firing
    /// <c>PlaybackStopped</c> — see <see cref="PlaybackProgressSnapshot"/>'s remarks for the
    /// verified source-level reason. When null, this falls back to reading the (normally empty)
    /// session fields directly, which is a degraded-but-safe result, not a crash.
    /// </param>
    /// <returns>The normalized playback event, or null if the event lacks a session or item.</returns>
    /// <remarks>
    /// <see cref="PlaybackEvent.SubtitleFormat"/> is intentionally left null here — extracting
    /// the actively-used subtitle stream requires matching <c>PlayState.SubtitleStreamIndex</c>
    /// against <c>Item.MediaStreams</c>, which is deferred to the plan that adds the
    /// subtitle-burn-in rule so it gets its own reviewed task and test coverage.
    /// </remarks>
    public static PlaybackEvent? FromEventArgs(PlaybackStopEventArgs args, PlaybackProgressSnapshot? progressSnapshot)
    {
        ArgumentNullException.ThrowIfNull(args);

        var session = args.Session;
        if (session is null || args.Item is null)
        {
            return null;
        }

        var transcodingInfo = session.TranscodingInfo;

        return new PlaybackEvent
        {
            SessionId = session.Id ?? string.Empty,
            ItemId = args.Item.Id.ToString(),
            Client = session.Client ?? string.Empty,
            DeviceName = session.DeviceName ?? string.Empty,
            PlayMethod = progressSnapshot?.PlayMethod ?? session.PlayState?.PlayMethod,
            TranscodeReasons = progressSnapshot?.TranscodeReasons ?? transcodingInfo?.TranscodeReasons ?? default,
            VideoCodec = progressSnapshot?.VideoCodec ?? transcodingInfo?.VideoCodec,
            AudioCodec = progressSnapshot?.AudioCodec ?? transcodingInfo?.AudioCodec,
            SubtitleFormat = null,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
