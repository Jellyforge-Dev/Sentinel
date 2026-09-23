using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// A <see cref="Diagnosis"/> joined with the context of the <see cref="PlaybackEvent"/> that
/// produced it, shaped for display rather than for re-insertion — this is a read model, not the
/// persistence entity.
/// </summary>
public sealed class DiagnosisSummary
{
    /// <summary>
    /// Gets the diagnosis row's own id.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Gets the diagnosis code, e.g. "VIDEO_CODEC_UNSUPPORTED".
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets how certain this diagnosis is.
    /// </summary>
    public required Confidence Confidence { get; init; }

    /// <summary>
    /// Gets the concrete facts backing this diagnosis.
    /// </summary>
    public required IReadOnlyList<string> Evidence { get; init; }

    /// <summary>
    /// Gets the plain-language explanation.
    /// </summary>
    public required string Explanation { get; init; }

    /// <summary>
    /// Gets what, if anything, the admin should do about it.
    /// </summary>
    public required string Recommendation { get; init; }

    /// <summary>
    /// Gets when this diagnosis was produced.
    /// </summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// Gets the id of the media item involved, as a string (matches how it's stored).
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Gets the client application name for the originating session.
    /// </summary>
    public required string Client { get; init; }

    /// <summary>
    /// Gets the device name for the originating session.
    /// </summary>
    public required string DeviceName { get; init; }

    /// <summary>
    /// Gets the Jellyfin username for the originating session, if one was reported.
    /// </summary>
    public required string UserName { get; init; }

    /// <summary>
    /// Gets the play method Jellyfin used for the originating session.
    /// </summary>
    public required string PlayMethod { get; init; }
}
