using System;
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
    /// <returns>The normalized playback event, or null if the event lacks a session or item.</returns>
    /// <remarks>
    /// <see cref="PlaybackEvent.SubtitleFormat"/> is intentionally left null here — extracting
    /// the actively-used subtitle stream requires matching <c>PlayState.SubtitleStreamIndex</c>
    /// against <c>Item.MediaStreams</c>, which is deferred to the plan that adds the
    /// subtitle-burn-in rule so it gets its own reviewed task and test coverage.
    /// </remarks>
    public static PlaybackEvent? FromEventArgs(PlaybackStopEventArgs args)
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
            PlayMethod = session.PlayState?.PlayMethod,
            TranscodeReasons = transcodingInfo?.TranscodeReasons ?? default,
            VideoCodec = transcodingInfo?.VideoCodec,
            AudioCodec = transcodingInfo?.AudioCodec,
            SubtitleFormat = null,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
