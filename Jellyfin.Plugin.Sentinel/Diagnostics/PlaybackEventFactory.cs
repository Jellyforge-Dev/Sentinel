using System;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Session;

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
    /// The most recent play-method/transcode-reason data for this play session — resolved by the
    /// caller (<c>PlaybackCollectorHostedService</c>) from a cache built out of
    /// <c>PlaybackProgress</c> events, including its own fallback for stops that never carry a
    /// <c>PlaySessionId</c> — or null if none was found. Required because Jellyfin always clears
    /// <c>Session.PlayState</c> and <c>Session.TranscodingInfo</c> before firing
    /// <c>PlaybackStopped</c> — see <see cref="PlaybackProgressSnapshot"/>'s remarks for the
    /// verified source-level reason. When null, this falls back to reading the session fields
    /// directly; in real Jellyfin those are guaranteed empty for the same reason the snapshot
    /// exists at all, so this path is deliberately degraded-but-safe rather than a recovery
    /// mechanism — it exists so a genuinely unresolved snapshot never crashes the handler, not
    /// because it is expected to produce useful data.
    /// </param>
    /// <returns>The normalized playback event, or null if the event lacks a session or item.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="PlaybackEvent.SubtitleFormat"/> is intentionally left null here — extracting
    /// the actively-used subtitle stream requires matching <c>PlayState.SubtitleStreamIndex</c>
    /// against <c>Item.MediaStreams</c>, which is deferred to the plan that adds the
    /// subtitle-burn-in rule so it gets its own reviewed task and test coverage.
    /// </para>
    /// <para>
    /// <c>PlayMethod</c> is forced to <see cref="PlayMethod.Transcode"/> when the resolved
    /// <c>TranscodeReasons</c> is non-default AND <c>PlayMethod</c> resolved to
    /// <see cref="PlayMethod.DirectPlay"/> or null — never for
    /// <see cref="PlayMethod.DirectStream"/>. This closes a bug confirmed on a real server
    /// (v0.1.3.0): a session with an active, verified ffmpeg transcode (video re-encoded, audio
    /// downmixed) still resolved with <c>PlayMethod = DirectPlay</c> next to a correctly
    /// non-zero <c>TranscodeReasons</c> in the same row — likely a later progress tick, during a
    /// mid-playback seek/audio-track change, reporting a contradicting <c>PlayMethod</c> that
    /// the cache's null-only merge protection (see <see cref="PlaybackProgressSnapshot"/>)
    /// didn't guard against. This is a heuristic, not a proven invariant: <c>DirectPlay</c>
    /// means Jellyfin served the raw file with no ffmpeg job, so a captured transcode reason
    /// contradicts it far more often than not — but the same field-misreporting failure mode
    /// that motivates this override at all could in principle also misreport a remux as
    /// <c>DirectPlay</c>, in which case this would still wrongly force it to <c>Transcode</c>.
    /// <c>DirectStream</c> (a remux: container changed, audio/video streams copied without
    /// re-encoding) is excluded because it legitimately carries non-zero <c>TranscodeReasons</c>
    /// (e.g. <c>ContainerNotSupported</c>, the very reason <c>DirectStream</c> was chosen over
    /// <c>DirectPlay</c>) whenever it's reported correctly — forcing it to <c>Transcode</c>
    /// unconditionally would misclassify every remux as a full transcode and fire
    /// <c>CONTAINER_UNSUPPORTED</c> (Confirmed confidence) for sessions that never re-encoded
    /// anything. A more robust fix — not yet implemented — would key this off
    /// <c>TranscodingInfo.IsVideoDirect</c>/<c>IsAudioDirect</c> (Jellyfin's own remux
    /// indicators, set independent of the client-reported <c>PlayMethod</c>) instead of the
    /// <c>PlayMethod</c> value itself.
    /// </para>
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
        var playMethod = progressSnapshot?.PlayMethod ?? session.PlayState?.PlayMethod;
        var transcodeReasons = progressSnapshot?.TranscodeReasons ?? transcodingInfo?.TranscodeReasons ?? default;

        if (transcodeReasons != default && playMethod != PlayMethod.DirectStream)
        {
            playMethod = PlayMethod.Transcode;
        }

        return new PlaybackEvent
        {
            SessionId = session.Id ?? string.Empty,
            ItemId = args.Item.Id.ToString(),
            Client = session.Client ?? string.Empty,
            DeviceName = session.DeviceName ?? string.Empty,
            UserName = session.UserName ?? string.Empty,
            PlayMethod = playMethod,
            TranscodeReasons = transcodeReasons,
            VideoCodec = progressSnapshot?.VideoCodec ?? transcodingInfo?.VideoCodec,
            AudioCodec = progressSnapshot?.AudioCodec ?? transcodingInfo?.AudioCodec,
            SubtitleFormat = null,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
