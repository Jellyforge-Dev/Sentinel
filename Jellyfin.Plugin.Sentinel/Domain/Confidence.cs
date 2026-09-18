namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// How certain a diagnosis is. Never a numeric percentage — there is no
/// statistically defensible way to derive one from a handful of rule matches.
/// </summary>
public enum Confidence
{
    /// <summary>No usable evidence.</summary>
    Unknown,

    /// <summary>Some evidence, but weak or ambiguous.</summary>
    Possible,

    /// <summary>Solid evidence pointing at one cause.</summary>
    Likely,

    /// <summary>Strong, consistent evidence pointing at one cause.</summary>
    VeryLikely,

    /// <summary>Directly observed, unambiguous cause.</summary>
    Confirmed
}
