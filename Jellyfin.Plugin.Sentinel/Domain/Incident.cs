using System;

namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// A deduplicated, lifecycle-tracked grouping of one or more <see cref="Diagnosis"/> records that
/// share the same fingerprint (rule code, media item, client, and device) — the Incident Engine's
/// unit of alerting, distinct from the raw per-playback <see cref="Diagnosis"/> rows it groups.
/// </summary>
public sealed class Incident
{
    /// <summary>Gets the database-assigned identifier.</summary>
    public required long Id { get; init; }

    /// <summary>Gets the diagnostic rule code this incident's fingerprint is keyed on.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the ID of the media item this incident's fingerprint is keyed on.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the Jellyfin client name this incident's fingerprint is keyed on.</summary>
    public required string Client { get; init; }

    /// <summary>Gets the device name this incident's fingerprint is keyed on.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Gets the Jellyfin username most recently affected by this incident.</summary>
    public required string UserName { get; init; }

    /// <summary>Gets the current lifecycle state.</summary>
    public required IncidentStatus Status { get; init; }

    /// <summary>Gets how many diagnoses have been linked to this incident, including the one that created it.</summary>
    public required int OccurrenceCount { get; init; }

    /// <summary>Gets the UTC time this incident was first created.</summary>
    public required DateTime FirstSeenUtc { get; init; }

    /// <summary>Gets the UTC time the most recent matching diagnosis was linked to this incident.</summary>
    public required DateTime LastSeenUtc { get; init; }

    /// <summary>Gets the UTC time an administrator acknowledged this incident, if they have.</summary>
    public DateTime? AcknowledgedAtUtc { get; init; }

    /// <summary>Gets the UTC time an administrator resolved this incident, if they have and it has not since reopened.</summary>
    public DateTime? ResolvedAtUtc { get; init; }
}
