using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Persistence;

/// <summary>
/// The outcome of <see cref="IncidentRepository.UpsertOnDiagnosis"/> — tells the caller whether
/// this specific call is the one that should trigger a notification (a brand-new incident, or one
/// that just reopened after being resolved) versus a plain occurrence-count increment on an
/// already-known, already-notified incident.
/// </summary>
/// <param name="IncidentId">The ID of the incident that was created, incremented, or reopened.</param>
/// <param name="IsNewOrReopened">Whether this specific call is the one that should trigger a notification.</param>
/// <param name="IsExcepted">Whether this incident's Code currently has an admin-configured "Expected" rule policy — callers must skip notification dispatch when this is true, even if <paramref name="IsNewOrReopened"/> is also true.</param>
public readonly record struct IncidentUpsertResult(long IncidentId, bool IsNewOrReopened, bool IsExcepted);

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
    /// <param name="userName">The Jellyfin username most recently affected — not part of the fingerprint, but written on every branch so <see cref="Incident.UserName"/> always reflects the most recent occurrence, matching <see cref="Incident.LastSeenUtc"/>'s semantics.</param>
    /// <param name="isExcepted">Whether this diagnosis's Code currently has an admin-configured "Expected" rule policy — see <see cref="SetRulePolicy"/>. Written on every branch, like <paramref name="userName"/>, so a policy change takes effect the next time that incident recurs.</param>
    /// <returns>The ID of the incident that was created, incremented, or reopened, and whether this call is the one that should trigger a notification.</returns>
    public IncidentUpsertResult UpsertOnDiagnosis(long diagnosisId, string code, string itemId, string client, string deviceName, string userName, bool isExcepted)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        long incidentId;
        bool isNewOrReopened;
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
                      SET Status = $reopened, OccurrenceCount = OccurrenceCount + 1, LastSeenUtc = $now, UserName = $userName, ResolvedAtUtc = NULL, IsExcepted = $isExcepted
                      WHERE Id = $id;
                      """
                    : """
                      UPDATE Incident SET OccurrenceCount = OccurrenceCount + 1, LastSeenUtc = $now, UserName = $userName, IsExcepted = $isExcepted WHERE Id = $id;
                      """;
#pragma warning restore CA2100
                updateCommand.Parameters.AddWithValue("$id", incidentId);
                updateCommand.Parameters.AddWithValue("$now", now);
                updateCommand.Parameters.AddWithValue("$userName", userName);
                updateCommand.Parameters.AddWithValue("$isExcepted", isExcepted);
                isNewOrReopened = existingStatus == nameof(IncidentStatus.Resolved);
                if (isNewOrReopened)
                {
                    updateCommand.Parameters.AddWithValue("$reopened", nameof(IncidentStatus.Reopened));
                }

                updateCommand.ExecuteNonQuery();
            }
            else
            {
                reader.Close();
                isNewOrReopened = true;
                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    """
                    INSERT INTO Incident (Code, ItemId, Client, DeviceName, UserName, Status, OccurrenceCount, FirstSeenUtc, LastSeenUtc, IsExcepted)
                    VALUES ($code, $itemId, $client, $deviceName, $userName, $detected, 1, $now, $now, $isExcepted);
                    SELECT last_insert_rowid();
                    """;
                insertCommand.Parameters.AddWithValue("$code", code);
                insertCommand.Parameters.AddWithValue("$itemId", itemId);
                insertCommand.Parameters.AddWithValue("$client", client);
                insertCommand.Parameters.AddWithValue("$deviceName", deviceName);
                insertCommand.Parameters.AddWithValue("$userName", userName);
                insertCommand.Parameters.AddWithValue("$detected", nameof(IncidentStatus.Detected));
                insertCommand.Parameters.AddWithValue("$now", now);
                insertCommand.Parameters.AddWithValue("$isExcepted", isExcepted);
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
        return new IncidentUpsertResult(incidentId, isNewOrReopened, isExcepted);
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
            SELECT Id, Code, ItemId, Client, DeviceName, UserName, Status, OccurrenceCount,
                   FirstSeenUtc, LastSeenUtc, AcknowledgedAtUtc, ResolvedAtUtc, ResolutionNote, IsExcepted
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
                UserName = reader.GetString(5),
                Status = Enum.Parse<IncidentStatus>(reader.GetString(6)),
                OccurrenceCount = reader.GetInt32(7),
                FirstSeenUtc = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                LastSeenUtc = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                AcknowledgedAtUtc = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ResolvedAtUtc = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ResolutionNote = reader.GetString(12),
                IsExcepted = reader.GetInt64(13) != 0
            });
        }

        return results;
    }

    /// <summary>
    /// Checks whether code currently has an admin-configured "Expected" policy, for the collector
    /// to pass into <see cref="UpsertOnDiagnosis"/>. Scoped to the rule code alone (server-wide, not
    /// per user or per media item) — see <see cref="SetRulePolicy"/>'s remarks for why.
    /// </summary>
    /// <param name="code">The diagnostic rule code.</param>
    /// <returns>True if this rule code is currently policed as "Expected".</returns>
    public bool IsCodeExpected(string code)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM RulePolicy WHERE Code = $code AND Action = 'Expected';";
        command.Parameters.AddWithValue("$code", code);
        return (long)command.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// Gets every rule code that currently has a non-default policy (i.e. is marked "Expected"),
    /// for the dashboard's rule-policy settings panel to pre-select against the full known-code list.
    /// </summary>
    /// <returns>The set of rule codes currently marked "Expected".</returns>
    public IReadOnlyCollection<string> GetExpectedCodes()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Code FROM RulePolicy WHERE Action = 'Expected';";

        var codes = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    /// <summary>
    /// Sets or replaces an incident's resolution note — free text on what ultimately fixed the
    /// underlying problem for the user, e.g. "enabled hardware transcoding".
    /// </summary>
    /// <param name="incidentId">The incident's ID.</param>
    /// <param name="note">The note text.</param>
    public void UpdateResolutionNote(long incidentId, string note)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Incident SET ResolutionNote = $note WHERE Id = $id;";
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$id", incidentId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Sets whether a diagnostic rule code is treated as "Expected" (suppressing notifications for
    /// it) or "Notify" (the default), and immediately updates every currently-open Incident row for
    /// that code to match. Deliberately scoped to the rule code alone, server-wide — not to a
    /// specific user or media item: live-server feedback said the previous per-user "mark this
    /// incident as expected" button still felt like it applied "per movie/show" one at a time,
    /// when what was wanted is a fixed, admin-configurable policy per diagnosis reason
    /// ("if: HDR tone-mapping then: expected") that applies everywhere that reason occurs, for
    /// anyone.
    /// </summary>
    /// <param name="code">The diagnostic rule code.</param>
    /// <param name="expected">True to mark this code "Expected" (suppressed); false to reset it to the default "Notify".</param>
    public void SetRulePolicy(string code, bool expected)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var writeCommand = connection.CreateCommand())
        {
            writeCommand.Transaction = transaction;

            // CA2100 flags this ternary the same way it flags the identical pattern in
            // IncidentRepository.UpsertOnDiagnosis — neither branch incorporates caller-supplied
            // text, only parameter placeholders bound below.
#pragma warning disable CA2100
            writeCommand.CommandText = expected
                ? """
                  INSERT INTO RulePolicy (Code, Action, CreatedAtUtc)
                  VALUES ($code, 'Expected', $now)
                  ON CONFLICT (Code) DO UPDATE SET Action = 'Expected';
                  """
                : "DELETE FROM RulePolicy WHERE Code = $code;";
#pragma warning restore CA2100
            writeCommand.Parameters.AddWithValue("$code", code);
            if (expected)
            {
                writeCommand.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            }

            writeCommand.ExecuteNonQuery();
        }

        using (var markCommand = connection.CreateCommand())
        {
            markCommand.Transaction = transaction;
            markCommand.CommandText = "UPDATE Incident SET IsExcepted = $isExcepted WHERE Code = $code;";
            markCommand.Parameters.AddWithValue("$isExcepted", expected);
            markCommand.Parameters.AddWithValue("$code", code);
            markCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Gets the ID of the most recently created diagnosis linked to this incident.
    /// </summary>
    /// <param name="incidentId">The incident's ID.</param>
    /// <returns>The most recent linked diagnosis's ID, or null if none are linked (should not happen in practice — every incident is created by <see cref="UpsertOnDiagnosis"/> linking at least one).</returns>
    public long? GetLatestDiagnosisId(long incidentId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.Id
            FROM IncidentDiagnosis link
            JOIN Diagnosis d ON d.Id = link.DiagnosisId
            WHERE link.IncidentId = $incidentId
            ORDER BY d.CreatedAtUtc DESC, d.Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$incidentId", incidentId);

        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : (long)result;
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
