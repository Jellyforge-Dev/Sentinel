using System;
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Persistence;

/// <summary>
/// Repository for persisting and retrieving <see cref="PlaybackEvent"/> records.
/// </summary>
public sealed class PlaybackEventRepository
{
    private readonly SentinelDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackEventRepository"/> class.
    /// </summary>
    /// <param name="database">The database instance to use for persistence.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="database"/> is null.</exception>
    public PlaybackEventRepository(SentinelDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Inserts a playback event into the database.
    /// </summary>
    /// <param name="playbackEvent">The playback event to insert.</param>
    /// <returns>The newly inserted row's ID.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="playbackEvent"/> is null.</exception>
    public long Insert(PlaybackEvent playbackEvent)
    {
        ArgumentNullException.ThrowIfNull(playbackEvent);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PlaybackEvent
                (SessionId, ItemId, Client, DeviceName, PlayMethod, TranscodeReasons, VideoCodec, AudioCodec, SubtitleFormat, CreatedAtUtc)
            VALUES
                ($sessionId, $itemId, $client, $deviceName, $playMethod, $transcodeReasons, $videoCodec, $audioCodec, $subtitleFormat, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$sessionId", playbackEvent.SessionId);
        command.Parameters.AddWithValue("$itemId", playbackEvent.ItemId);
        command.Parameters.AddWithValue("$client", playbackEvent.Client);
        command.Parameters.AddWithValue("$deviceName", playbackEvent.DeviceName);
        command.Parameters.AddWithValue("$playMethod", playbackEvent.PlayMethod?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("$transcodeReasons", (long)playbackEvent.TranscodeReasons);
        command.Parameters.AddWithValue("$videoCodec", (object?)playbackEvent.VideoCodec ?? System.DBNull.Value);
        command.Parameters.AddWithValue("$audioCodec", (object?)playbackEvent.AudioCodec ?? System.DBNull.Value);
        command.Parameters.AddWithValue("$subtitleFormat", (object?)playbackEvent.SubtitleFormat ?? System.DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", playbackEvent.CreatedAtUtc.ToString("O"));

        return (long)command.ExecuteScalar()!;
    }
}
