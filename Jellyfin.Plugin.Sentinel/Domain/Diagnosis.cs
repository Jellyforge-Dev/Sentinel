using System.Collections.Generic;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// One rule engine result. Never constructed without evidence.
/// </summary>
public sealed class Diagnosis
{
    /// <summary>Gets the stable machine-readable diagnosis code, e.g. "VIDEO_CODEC_UNSUPPORTED".</summary>
    public required string Code { get; init; }

    /// <summary>Gets how certain this diagnosis is.</summary>
    public required Confidence Confidence { get; init; }

    /// <summary>Gets the concrete facts from the playback event that support this diagnosis.</summary>
    public required IReadOnlyList<string> Evidence { get; init; }

    /// <summary>Gets the plain-language explanation of what happened.</summary>
    public required string Explanation { get; init; }

    /// <summary>Gets what the admin should do about it, if anything.</summary>
    public required string Recommendation { get; init; }
}
