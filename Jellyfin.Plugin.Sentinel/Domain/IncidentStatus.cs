namespace Jellyfin.Plugin.Sentinel.Domain;

/// <summary>
/// The lifecycle state of an <see cref="Incident"/>. The master plan's "Investigating" sub-state
/// is deliberately not modeled here as a distinct value — nothing in this codebase transitions
/// into or out of it automatically, so it is left as a UI-only concept if the dashboard ever wants
/// it, rather than adding a database value with no code path that sets or clears it.
/// </summary>
public enum IncidentStatus
{
    /// <summary>Newly created; nobody has acknowledged it yet.</summary>
    Detected,

    /// <summary>An administrator has acknowledged this incident is known.</summary>
    Acknowledged,

    /// <summary>An administrator marked this incident as resolved.</summary>
    Resolved,

    /// <summary>A previously resolved incident's fingerprint recurred.</summary>
    Reopened
}
