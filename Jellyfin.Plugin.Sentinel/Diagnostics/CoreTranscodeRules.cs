using System.Collections.Generic;
using Jellyfin.Plugin.Sentinel.Domain;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Sentinel.Diagnostics;

/// <summary>
/// The Confirmed-confidence rules that need nothing beyond the <see cref="TranscodeReason"/>
/// flags already captured on a <see cref="PlaybackEvent"/> — master plan Section 14, rules
/// #2-#4, #6, #7, plus #10 (missing-reason handling).
/// </summary>
public static class CoreTranscodeRules
{
    /// <summary>
    /// Gets the complete set of core transcode diagnostic rules.
    /// </summary>
    public static IReadOnlyList<DiagnosticRule> All { get; } = new List<DiagnosticRule>
    {
        new(
            Code: "VIDEO_CODEC_UNSUPPORTED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.VideoCodecNotSupported),
            Confidence: Confidence.Confirmed,
            Explain: e => "Your device can't play this video's codec natively, so Jellyfin had to convert it on the fly.",
            Recommendation: "No action needed unless playback quality or server load is a problem — this is expected for this client/codec combination."),

        new(
            Code: "AUDIO_CODEC_UNSUPPORTED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.AudioCodecNotSupported),
            Confidence: Confidence.Confirmed,
            Explain: e => "Your device can't decode this audio track natively, so Jellyfin had to transcode the audio.",
            Recommendation: "No action needed — this is expected behavior for this client/audio-codec combination."),

        new(
            Code: "CONTAINER_UNSUPPORTED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.ContainerNotSupported),
            Confidence: Confidence.Confirmed,
            Explain: e => "Your device doesn't support this file's container format, so Jellyfin had to remux or transcode it.",
            Recommendation: "No action needed — this is expected behavior for this client/container combination."),

        new(
            Code: "SECONDARY_AUDIO_UNSUPPORTED",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.SecondaryAudioNotSupported),
            Confidence: Confidence.Confirmed,
            Explain: e => "This file has a secondary audio track your device can't handle alongside the primary one, forcing a transcode.",
            Recommendation: "If this happens often for this title, consider removing or re-encoding the secondary audio track."),

        new(
            Code: "TOO_MANY_STREAMS",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode
                && e.TranscodeReasons.HasFlag(TranscodeReason.StreamCountExceedsLimit),
            Confidence: Confidence.Confirmed,
            Explain: e => "This file has more audio/subtitle streams than your client can handle at once, so Jellyfin had to transcode it.",
            Recommendation: "Consider trimming unused audio/subtitle tracks from this file."),

        new(
            Code: "TRANSCODE_REASON_MISSING",
            Predicate: e => e.PlayMethod == PlayMethod.Transcode && e.TranscodeReasons == default,
            Confidence: Confidence.Unknown,
            Explain: e => "Jellyfin transcoded this playback but didn't record why — this can happen due to a known Jellyfin logging gap, not necessarily a configuration problem.",
            Recommendation: "No specific action — Sentinel doesn't have enough information for a confident diagnosis here."),
    };
}
