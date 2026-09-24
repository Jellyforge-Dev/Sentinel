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
                UserName TEXT NOT NULL,
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
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (PlaybackEventId) REFERENCES PlaybackEvent(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_Diagnosis_Code ON Diagnosis(Code);
            CREATE INDEX IF NOT EXISTS IX_Diagnosis_PlaybackEventId ON Diagnosis(PlaybackEventId);

            CREATE TABLE IF NOT EXISTS Incident (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Code TEXT NOT NULL,
                ItemId TEXT NOT NULL,
                Client TEXT NOT NULL,
                DeviceName TEXT NOT NULL,
                UserName TEXT NOT NULL,
                Status TEXT NOT NULL,
                OccurrenceCount INTEGER NOT NULL,
                FirstSeenUtc TEXT NOT NULL,
                LastSeenUtc TEXT NOT NULL,
                AcknowledgedAtUtc TEXT NULL,
                ResolvedAtUtc TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Incident_Fingerprint ON Incident(Code, ItemId, Client, DeviceName)
                WHERE Status != 'Resolved';
            CREATE INDEX IF NOT EXISTS IX_Incident_LastSeenUtc ON Incident(LastSeenUtc);

            CREATE TABLE IF NOT EXISTS IncidentDiagnosis (
                IncidentId INTEGER NOT NULL,
                DiagnosisId INTEGER NOT NULL,
                PRIMARY KEY (IncidentId, DiagnosisId),
                FOREIGN KEY (IncidentId) REFERENCES Incident(Id),
                FOREIGN KEY (DiagnosisId) REFERENCES Diagnosis(Id)
            );

            CREATE TABLE IF NOT EXISTS NotifiedPluginUpdate (
                PluginId TEXT NOT NULL,
                Version TEXT NOT NULL,
                NotifiedAtUtc TEXT NOT NULL,
                PRIMARY KEY (PluginId, Version)
            );

            CREATE TABLE IF NOT EXISTS IncidentException (
                UserName TEXT NOT NULL PRIMARY KEY,
                Reason TEXT NOT NULL DEFAULT '',
                CreatedAtUtc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        DropColumnIfExists(connection, "Diagnosis", "Explanation");
        DropColumnIfExists(connection, "Diagnosis", "Recommendation");

        AddColumnIfMissing(connection, "PlaybackEvent", "UserName", "UserName TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Incident", "UserName", "UserName TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Incident", "ResolutionNote", "ResolutionNote TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Incident", "IsExcepted", "IsExcepted INTEGER NOT NULL DEFAULT 0");
        MigrateIncidentExceptionToUserScoped(connection);
    }

    // v0.1.6.1 shipped IncidentException keyed by (Code, UserName) — feedback from the first
    // real live-server test showed this felt "per movie/show" rather than a real, general
    // exception, since a single user's transcoding can trip several different rule codes across
    // different titles. Broadened here to a single UserName-scoped exception, matching the actual
    // real-world use case ("this user always transcodes via a GPU — never treat that as a
    // problem"). This migration preserves every user who was ever marked excepted under the old
    // shape (deduplicated, keeping the earliest CreatedAtUtc) rather than silently discarding
    // them, even though the feature had shipped only one release earlier.
    private static void MigrateIncidentExceptionToUserScoped(SqliteConnection connection)
    {
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('IncidentException') WHERE name = 'Code';";
            var hasOldCodeColumn = (long)checkCommand.ExecuteScalar()! > 0;
            if (!hasOldCodeColumn)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();

        using (var renameCommand = connection.CreateCommand())
        {
            renameCommand.Transaction = transaction;
            renameCommand.CommandText = "ALTER TABLE IncidentException RENAME TO IncidentException_v1;";
            renameCommand.ExecuteNonQuery();
        }

        using (var createCommand = connection.CreateCommand())
        {
            createCommand.Transaction = transaction;
            createCommand.CommandText =
                """
                CREATE TABLE IncidentException (
                    UserName TEXT NOT NULL PRIMARY KEY,
                    Reason TEXT NOT NULL DEFAULT '',
                    CreatedAtUtc TEXT NOT NULL
                );
                """;
            createCommand.ExecuteNonQuery();
        }

        using (var copyCommand = connection.CreateCommand())
        {
            copyCommand.Transaction = transaction;
            copyCommand.CommandText =
                """
                INSERT INTO IncidentException (UserName, Reason, CreatedAtUtc)
                SELECT UserName, MAX(Reason), MIN(CreatedAtUtc)
                FROM IncidentException_v1
                GROUP BY UserName;
                """;
            copyCommand.ExecuteNonQuery();
        }

        using (var dropCommand = connection.CreateCommand())
        {
            dropCommand.Transaction = transaction;
            dropCommand.CommandText = "DROP TABLE IncidentException_v1;";
            dropCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // CA2100 flags the interpolated CommandText below because SQLite has no parameter syntax for
    // identifiers (table/column names). table/column are always fixed literal strings passed by
    // callers in this file — never caller-supplied or externally sourced — so this is a false
    // positive for this specific, narrowly-scoped method.
#pragma warning disable CA2100
    private static void DropColumnIfExists(SqliteConnection connection, string table, string column)
    {
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
            checkCommand.Parameters.AddWithValue("$column", column);
            var exists = (long)checkCommand.ExecuteScalar()! > 0;
            if (!exists)
            {
                return;
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
        alterCommand.ExecuteNonQuery();
    }
#pragma warning restore CA2100

    // See DropColumnIfExists's comment above for why this interpolation is safe: table/column here
    // are always fixed literal strings passed by callers in this file, never caller-supplied.
#pragma warning disable CA2100
    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string columnDefinitionSql)
    {
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
            checkCommand.Parameters.AddWithValue("$column", column);
            var exists = (long)checkCommand.ExecuteScalar()! > 0;
            if (exists)
            {
                return;
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {columnDefinitionSql};";
        alterCommand.ExecuteNonQuery();
    }
#pragma warning restore CA2100
}
