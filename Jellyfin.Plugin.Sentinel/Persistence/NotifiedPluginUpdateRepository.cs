using System;

namespace Jellyfin.Plugin.Sentinel.Persistence;

/// <summary>
/// Tracks which (plugin, version) pairs Sentinel has already sent an "update available"
/// notification for, so the periodic check in <see cref="Collector.PluginUpdateMonitorHostedService"/>
/// never re-notifies for the same update twice.
/// </summary>
public sealed class NotifiedPluginUpdateRepository
{
    private readonly SentinelDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotifiedPluginUpdateRepository"/> class.
    /// </summary>
    /// <param name="database">The database instance to use for persistence.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="database"/> is null.</exception>
    public NotifiedPluginUpdateRepository(SentinelDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Gets whether this exact plugin+version has already been notified about.
    /// </summary>
    /// <param name="pluginId">The plugin's ID.</param>
    /// <param name="version">The available version.</param>
    /// <returns>True if already notified; otherwise false.</returns>
    public bool HasBeenNotified(Guid pluginId, string version)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM NotifiedPluginUpdate WHERE PluginId = $pluginId AND Version = $version;";
        command.Parameters.AddWithValue("$pluginId", pluginId.ToString());
        command.Parameters.AddWithValue("$version", version);
        return (long)command.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// Records that a plugin+version has now been notified about.
    /// </summary>
    /// <param name="pluginId">The plugin's ID.</param>
    /// <param name="version">The available version.</param>
    public void MarkNotified(Guid pluginId, string version)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO NotifiedPluginUpdate (PluginId, Version, NotifiedAtUtc)
            VALUES ($pluginId, $version, $now)
            ON CONFLICT (PluginId, Version) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$pluginId", pluginId.ToString());
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets whether any plugin-update notification was recorded within the given window —
    /// used to decide whether a "restart pending" notification may plausibly mention a recent
    /// plugin update as a possible cause.
    /// </summary>
    /// <param name="within">How far back to look.</param>
    /// <returns>True if at least one notification was recorded within the window.</returns>
    public bool HasRecentNotification(TimeSpan within)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM NotifiedPluginUpdate WHERE NotifiedAtUtc >= $cutoff;";
        command.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.Subtract(within).ToString("O"));
        return (long)command.ExecuteScalar()! > 0;
    }
}
