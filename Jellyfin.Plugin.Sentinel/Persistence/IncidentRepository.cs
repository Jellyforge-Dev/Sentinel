using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Persistence;

/// <summary>
/// Repository for the Incident Engine's dedup/lifecycle logic — groups <see cref="Diagnosis"/>
/// rows sharing the same fingerprint (rule code, item, client, device) into a single tracked
/// <see cref="Incident"/> rather than alerting once per raw diagnosis.
/// </summary>
public sealed class IncidentRepository
{
    private readonly SentinelDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentRepository"/> class.
    /// </summary>
    /// <param name="database">The database instance to use for persistence.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="database"/> is null.</exception>
    public IncidentRepository(SentinelDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Finds the most recent incident matching this fingerprint and increments it, reopens it if
    /// it was resolved, or creates a new one if none exists — then links the given diagnosis to it.
    /// </summary>
    /// <param name="diagnosisId">The ID of the diagnosis row that triggered this upsert.</param>
    /// <param name="code">The diagnostic rule code.</param>
    /// <param name="itemId">The media item's ID.</param>
    /// <param name="client">The Jellyfin client name.</param>
    /// <param name="deviceName">The device name.</param>
    /// <returns>The ID of the incident that was created, incremented, or reopened.</returns>
    public long UpsertOnDiagnosis(long diagnosisId, string code, string itemId, string client, string deviceName)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        long incidentId;
        var now = DateTime.UtcNow.ToString("O");

        using (var findCommand = connection.CreateCommand())
        {
            findCommand.Transaction = transaction;
            findCommand.CommandText =
                """
                SELECT Id, Status FROM Incident
                WHERE Code = $code AND ItemId = $itemId AND Client = $client AND DeviceName = $deviceName
                ORDER BY Id DESC LIMIT 1;
                """;
            findCommand.Parameters.AddWithValue("$code", code);
            findCommand.Parameters.AddWithValue("$itemId", itemId);
            findCommand.Parameters.AddWithValue("$client", client);
            findCommand.Parameters.AddWithValue("$deviceName", deviceName);

            using var reader = findCommand.ExecuteReader();
            if (reader.Read())
            {
                incidentId = reader.GetInt64(0);
                var existingStatus = reader.GetString(1);
                reader.Close();

                using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;

                // CA2100 flags this assignment because it cannot tell the ternary picks between two
                // fixed literal SQL strings — neither branch incorporates caller-supplied text, only
                // parameter placeholders bound below, so this is a false positive.
#pragma warning disable CA2100
                updateCommand.CommandText = existingStatus == nameof(IncidentStatus.Resolved)
                    ? """
                      UPDATE Incident
                      SET Status = $reopened, OccurrenceCount = OccurrenceCount + 1, LastSeenUtc = $now, ResolvedAtUtc = NULL
                      WHERE Id = $id;
                      """
                    : """
                      UPDATE Incident SET OccurrenceCount = OccurrenceCount + 1, LastSeenUtc = $now WHERE Id = $id;
                      """;
#pragma warning restore CA2100
                updateCommand.Parameters.AddWithValue("$id", incidentId);
                updateCommand.Parameters.AddWithValue("$now", now);
                if (existingStatus == nameof(IncidentStatus.Resolved))
                {
                    updateCommand.Parameters.AddWithValue("$reopened", nameof(IncidentStatus.Reopened));
                }

                updateCommand.ExecuteNonQuery();
            }
            else
            {
                reader.Close();
                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    """
                    INSERT INTO Incident (Code, ItemId, Client, DeviceName, Status, OccurrenceCount, FirstSeenUtc, LastSeenUtc)
                    VALUES ($code, $itemId, $client, $deviceName, $detected, 1, $now, $now);
                    SELECT last_insert_rowid();
                    """;
                insertCommand.Parameters.AddWithValue("$code", code);
                insertCommand.Parameters.AddWithValue("$itemId", itemId);
                insertCommand.Parameters.AddWithValue("$client", client);
                insertCommand.Parameters.AddWithValue("$deviceName", deviceName);
                insertCommand.Parameters.AddWithValue("$detected", nameof(IncidentStatus.Detected));
                insertCommand.Parameters.AddWithValue("$now", now);
                incidentId = (long)insertCommand.ExecuteScalar()!;
            }
        }

        using (var linkCommand = connection.CreateCommand())
        {
            linkCommand.Transaction = transaction;
            linkCommand.CommandText = "INSERT INTO IncidentDiagnosis (IncidentId, DiagnosisId) VALUES ($incidentId, $diagnosisId);";
            linkCommand.Parameters.AddWithValue("$incidentId", incidentId);
            linkCommand.Parameters.AddWithValue("$diagnosisId", diagnosisId);
            linkCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return incidentId;
    }

    /// <summary>
    /// Gets the most recently updated incidents, most recently updated first.
    /// </summary>
    /// <param name="limit">The maximum number of incidents to return. Must be positive.</param>
    /// <returns>A read-only list of incidents, most recently updated first.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="limit"/> is not positive.</exception>
    public IReadOnlyList<Incident> GetRecent(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Code, ItemId, Client, DeviceName, Status, OccurrenceCount,
                   FirstSeenUtc, LastSeenUtc, AcknowledgedAtUtc, ResolvedAtUtc
            FROM Incident
            ORDER BY LastSeenUtc DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<Incident>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Incident
            {
                Id = reader.GetInt64(0),
                Code = reader.GetString(1),
                ItemId = reader.GetString(2),
                Client = reader.GetString(3),
                DeviceName = reader.GetString(4),
                Status = Enum.Parse<IncidentStatus>(reader.GetString(5)),
                OccurrenceCount = reader.GetInt32(6),
                FirstSeenUtc = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                LastSeenUtc = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                AcknowledgedAtUtc = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ResolvedAtUtc = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }

        return results;
    }

    /// <summary>
    /// Marks an incident as acknowledged.
    /// </summary>
    /// <param name="incidentId">The incident's ID.</param>
    public void Acknowledge(long incidentId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Incident SET Status = $status, AcknowledgedAtUtc = $now WHERE Id = $id;";
        command.Parameters.AddWithValue("$status", nameof(IncidentStatus.Acknowledged));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", incidentId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Marks an incident as resolved.
    /// </summary>
    /// <param name="incidentId">The incident's ID.</param>
    public void Resolve(long incidentId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Incident SET Status = $status, ResolvedAtUtc = $now WHERE Id = $id;";
        command.Parameters.AddWithValue("$status", nameof(IncidentStatus.Resolved));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", incidentId);
        command.ExecuteNonQuery();
    }
}
