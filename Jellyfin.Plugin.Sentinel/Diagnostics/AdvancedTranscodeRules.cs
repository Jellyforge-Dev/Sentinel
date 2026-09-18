using System.Collections.Generic;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// Diagnostic rules that infer a cause from a <em>combination</em> of <see cref="TranscodeReason"/>
/// flags rather than a single direct match — master plan Section 14, rules #5, #8, #12, #13, #16.
/// Each of these needs no data beyond what <see cref="CoreTranscodeRules"/> already uses
/// (<c>PlayMethod</c> and <c>TranscodeReasons</c>), which is why they're implemented now rather
/// than deferred alongside the rules that need new data collection.
/// </summary>
public static class AdvancedTranscodeRules
{
    private const TranscodeReason BitrateOnlyReasons =
        TranscodeReason.ContainerBitrateExceedsLimit
        | TranscodeReason.VideoBitrateNotSupported
        | TranscodeReason.AudioBitrateNotSupported;

    private const TranscodeReason ResolutionOrBitrateReasons =
        BitrateOnlyReasons | TranscodeReason.VideoResolutionNotSupported;

    /// <summary>
    /// Gets the complete set of advanced (combination-inferred) transcode diagnostic rules.
    /// </summary>
    public static IReadOnlyList<DiagnosticRule> All { get; } = new List<DiagnosticRule>
    {
        new(
            Code: "EXTERNAL_AUDIO_FORCED_TRANSCODE",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.AudioIsExternal),
            Confidence: Confidence.Likely,
            Explain: e => "This file uses an external audio track, which some clients can't play alongside video without a transcode.",
            Recommendation: "If this happens often for this title, consider muxing the external audio track into the main file."),

        new(
            Code: "HDR_TONE_MAPPING_TRANSCODE",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.VideoRangeTypeNotSupported),
            Confidence: Confidence.Likely,
            Explain: e => "This video's HDR format (or other dynamic range type) isn't supported by your device, so Jellyfin had to convert it (typically including tone-mapping).",
            Recommendation: "No action needed unless playback quality or server load is a problem — this is expected for this client/HDR-format combination."),

        new(
            // Master plan item #16 specifically says "with no other reason" — unlike
            // EXTERNAL_AUDIO_FORCED_TRANSCODE/HDR_TONE_MAPPING_TRANSCODE above, this rule
            // requires exact equality (AudioChannelsNotSupported and nothing else), not just
            // HasFlag, so it never co-fires alongside a stronger, more specific Confirmed-tier
            // rule from CoreTranscodeRules (e.g. VideoCodecNotSupported) for the same event.
            Code: "AUDIO_CHANNEL_DOWNMIX",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons == TranscodeReason.AudioChannelsNotSupported,
            Confidence: Confidence.Possible,
            Explain: e => "This file's audio has more channels than your device/output supports, so Jellyfin had to downmix (and transcode) it.",
            Recommendation: "No action needed — this is expected behavior for this client/channel-layout combination."),

        new(
            Code: "BITRATE_CAP_EXCEEDED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons != default
                && (e.TranscodeReasons & ~BitrateOnlyReasons) == default,
            Confidence: Confidence.Possible,
            Explain: e => "This file's bitrate is above what your connection or device profile allows, so Jellyfin had to transcode it down — no codec or format mismatch was involved.",
            Recommendation: "If this happens often, consider a lower-bitrate encode of this title or checking your network/device bitrate limit setting."),

        new(
            Code: "RESOLUTION_DOWNSCALE_ONLY",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.VideoResolutionNotSupported)
                && (e.TranscodeReasons & ~ResolutionOrBitrateReasons) == default,
            Confidence: Confidence.Likely,
            Explain: e => "This video's resolution is above what your connection or device allows, so Jellyfin had to downscale (and transcode) it — no codec or format mismatch was involved.",
            Recommendation: "This is a network/device limit, not a format problem — a lower-resolution version of this title would direct play."),
    };
}
