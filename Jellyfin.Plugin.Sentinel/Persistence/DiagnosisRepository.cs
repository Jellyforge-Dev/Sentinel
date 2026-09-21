using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Localization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Persistence;

/// <summary>
/// Repository for persisting and retrieving <see cref="Diagnosis"/> records.
/// </summary>
public sealed partial class DiagnosisRepository
{
    private readonly SentinelDatabase _database;
    private readonly LocalizationService _localizationService;
    private readonly ILogger<DiagnosisRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosisRepository"/> class.
    /// </summary>
    /// <param name="database">The database instance to use for persistence.</param>
    /// <param name="localizationService">Used to translate diagnosis codes into explanation/recommendation text in <see cref="GetRecent"/>.</param>
    /// <param name="logger">Used to log a row that fails to deserialize in <see cref="GetRecent"/> without aborting the whole read.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="database"/>, <paramref name="localizationService"/>, or <paramref name="logger"/> is null.</exception>
    public DiagnosisRepository(SentinelDatabase database, LocalizationService localizationService, ILogger<DiagnosisRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _localizationService = localizationService;
        _logger = logger;
    }

    /// <summary>
    /// Inserts a diagnosis record into the database.
    /// </summary>
    /// <param name="playbackEventId">The ID of the playback event this diagnosis relates to.</param>
    /// <param name="diagnosis">The diagnosis record to insert.</param>
    /// <returns>The newly inserted row's ID.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="diagnosis"/> is null.</exception>
    public long Insert(long playbackEventId, Diagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Diagnosis
                (PlaybackEventId, Code, Confidence, EvidenceJson, CreatedAtUtc)
            VALUES
                ($playbackEventId, $code, $confidence, $evidenceJson, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$playbackEventId", playbackEventId);
        command.Parameters.AddWithValue("$code", diagnosis.Code);
        command.Parameters.AddWithValue("$confidence", diagnosis.Confidence.ToString());
        command.Parameters.AddWithValue("$evidenceJson", JsonSerializer.Serialize(diagnosis.Evidence));
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O"));

        return (long)command.ExecuteScalar()!;
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

    /// <summary>
    /// Retrieves the most recent diagnoses, joined with their originating playback event's
    /// context, most recent first. Intended for the plugin's own dashboard display — not a
    /// general-purpose query, hence the fixed ordering and no filtering.
    /// </summary>
    /// <param name="limit">The maximum number of diagnoses to return. Must be positive.</param>
    /// <param name="language">The language to translate each diagnosis's explanation and recommendation into.</param>
    /// <returns>A read-only list of diagnosis summaries, most recent first.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="limit"/> is not positive.</exception>
    public IReadOnlyList<DiagnosisSummary> GetRecent(int limit, SupportedLanguage language)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.Id, d.Code, d.Confidence, d.EvidenceJson, d.CreatedAtUtc,
                   p.ItemId, p.Client, p.DeviceName, p.PlayMethod
            FROM Diagnosis d
            JOIN PlaybackEvent p ON p.Id = d.PlaybackEventId
            ORDER BY d.CreatedAtUtc DESC, d.Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<DiagnosisSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var code = reader.GetString(1);

            // A single malformed row (a future enum rename, a partial write from a power loss)
            // must not take down the whole dashboard — an admin who most needs to see the other
            // rows would otherwise see nothing at all. Every value Insert() itself writes today is
            // always well-formed, so this is a forward-looking guard, not a known failure mode.
            try
            {
                results.Add(new DiagnosisSummary
                {
                    Id = id,
                    Code = code,
                    Confidence = Enum.Parse<Confidence>(reader.GetString(2)),
                    Evidence = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? new List<string>(),
                    Explanation = _localizationService.Translate($"{code}_EXPLANATION", language),
                    Recommendation = _localizationService.Translate($"{code}_RECOMMENDATION", language),
                    CreatedAtUtc = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    ItemId = reader.GetString(5),
                    Client = reader.GetString(6),
                    DeviceName = reader.GetString(7),
                    PlayMethod = reader.GetString(8)
                });
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or OverflowException)
            {
                LogMalformedDiagnosisRowSkipped(_logger, id, ex.Message, ex);
            }
        }

        return results;
    }

    /// <summary>
    /// Retrieves a single diagnosis by its ID, translated into the given language.
    /// </summary>
    /// <param name="diagnosisId">The diagnosis row's ID.</param>
    /// <param name="language">The language to translate the explanation and recommendation into.</param>
    /// <returns>The diagnosis summary, or null if no row with this ID exists or it could not be read back.</returns>
    public DiagnosisSummary? GetById(long diagnosisId, SupportedLanguage language)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.Id, d.Code, d.Confidence, d.EvidenceJson, d.CreatedAtUtc,
                   p.ItemId, p.Client, p.DeviceName, p.PlayMethod
            FROM Diagnosis d
            JOIN PlaybackEvent p ON p.Id = d.PlaybackEventId
            WHERE d.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", diagnosisId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var id = reader.GetInt64(0);
        var code = reader.GetString(1);

        try
        {
            return new DiagnosisSummary
            {
                Id = id,
                Code = code,
                Confidence = Enum.Parse<Confidence>(reader.GetString(2)),
                Evidence = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? new List<string>(),
                Explanation = _localizationService.Translate($"{code}_EXPLANATION", language),
                Recommendation = _localizationService.Translate($"{code}_RECOMMENDATION", language),
                CreatedAtUtc = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ItemId = reader.GetString(5),
                Client = reader.GetString(6),
                DeviceName = reader.GetString(7),
                PlayMethod = reader.GetString(8)
            };
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or OverflowException)
        {
            LogMalformedDiagnosisRowSkipped(_logger, id, ex.Message, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel: skipped Diagnosis row {Id} in GetRecent — it could not be read back: {Message}")]
    private static partial void LogMalformedDiagnosisRowSkipped(ILogger logger, long id, string message, Exception exception);
}
