using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Sentinel.Domain;

namespace Jellyfin.Plugin.Sentinel.Persistence;

/// <summary>
/// Repository for persisting and retrieving <see cref="Diagnosis"/> records.
/// </summary>
public sealed class DiagnosisRepository
{
    private readonly SentinelDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosisRepository"/> class.
    /// </summary>
    /// <param name="database">The database instance to use for persistence.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="database"/> is null.</exception>
    public DiagnosisRepository(SentinelDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Inserts a diagnosis record into the database.
    /// </summary>
    /// <param name="playbackEventId">The ID of the playback event this diagnosis relates to.</param>
    /// <param name="diagnosis">The diagnosis record to insert.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="diagnosis"/> is null.</exception>
    public void Insert(long playbackEventId, Diagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Diagnosis
                (PlaybackEventId, Code, Confidence, EvidenceJson, Explanation, Recommendation, CreatedAtUtc)
            VALUES
                ($playbackEventId, $code, $confidence, $evidenceJson, $explanation, $recommendation, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$playbackEventId", playbackEventId);
        command.Parameters.AddWithValue("$code", diagnosis.Code);
        command.Parameters.AddWithValue("$confidence", diagnosis.Confidence.ToString());
        command.Parameters.AddWithValue("$evidenceJson", JsonSerializer.Serialize(diagnosis.Evidence));
        command.Parameters.AddWithValue("$explanation", diagnosis.Explanation);
        command.Parameters.AddWithValue("$recommendation", diagnosis.Recommendation);
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O"));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Retrieves all diagnosis records for a specific playback event.
    /// </summary>
    /// <param name="playbackEventId">The ID of the playback event to retrieve diagnoses for.</param>
    /// <returns>A read-only list of tuples containing the diagnosis code and confidence level.</returns>
    public IReadOnlyList<(string Code, string Confidence)> GetAllForPlaybackEvent(long playbackEventId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Code, Confidence FROM Diagnosis WHERE PlaybackEventId = $playbackEventId;";
        command.Parameters.AddWithValue("$playbackEventId", playbackEventId);

        var results = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }
}
