using System;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// One deterministic rule: if <see cref="Predicate"/> matches a <see cref="PlaybackEvent"/>,
/// the rule engine produces a <see cref="Diagnosis"/> using the other fields.
/// </summary>
/// <param name="Code">Stable machine-readable diagnosis code, e.g. "VIDEO_CODEC_UNSUPPORTED".</param>
/// <param name="Predicate">Whether this rule matches a given playback event.</param>
/// <param name="Confidence">How certain this diagnosis is when the predicate matches.</param>
/// <param name="Explain">Produces the plain-language explanation for a matching event.</param>
/// <param name="Recommendation">What the admin should do about it, if anything.</param>
public sealed record DiagnosticRule(
    string Code,
    Func<PlaybackEvent, bool> Predicate,
    Confidence Confidence,
    Func<PlaybackEvent, string> Explain,
    string Recommendation);
