using System;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Sentinel.Persistence;

/// <summary>
/// Sentinel's own SQLite database — fully separate from Jellyfin's database.
/// </summary>
public sealed class SentinelDatabase
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="SentinelDatabase"/> class.
    /// </summary>
    /// <param name="databasePath">The file path where the SQLite database is stored.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="databasePath"/> is null.</exception>
    public SentinelDatabase(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        _connectionString = $"Data Source={databasePath}";
        Initialize();
    }

    /// <summary>
    /// Opens a new connection to the SQLite database.
    /// </summary>
    /// <returns>An open <see cref="SqliteConnection"/> to the database.</returns>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragmaCommand = connection.CreateCommand())
        {
            // SQLite does not enforce FOREIGN KEY constraints unless explicitly turned on
            // per-connection, so every connection opened through this single choke point
            // gets it enabled.
            pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
            pragmaCommand.ExecuteNonQuery();
        }

        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS PlaybackEvent (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                Client TEXT NOT NULL,
                DeviceName TEXT NOT NULL,
                PlayMethod TEXT NOT NULL,
                TranscodeReasons INTEGER NOT NULL,
                VideoCodec TEXT NULL,
                AudioCodec TEXT NULL,
                SubtitleFormat TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PlaybackEvent_Item_Client
                ON PlaybackEvent(ItemId, Client, CreatedAtUtc);

            CREATE TABLE IF NOT EXISTS Diagnosis (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlaybackEventId INTEGER NOT NULL,
                Code TEXT NOT NULL,
                Confidence TEXT NOT NULL,
                EvidenceJson TEXT NOT NULL,
                Explanation TEXT NOT NULL,
                Recommendation TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (PlaybackEventId) REFERENCES PlaybackEvent(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_Diagnosis_Code ON Diagnosis(Code);
            CREATE INDEX IF NOT EXISTS IX_Diagnosis_PlaybackEventId ON Diagnosis(PlaybackEventId);
            """;
        command.ExecuteNonQuery();
    }
}
