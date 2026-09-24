using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// Sends a <see cref="NotificationMessage"/> to an admin-configured Discord webhook URL, as a
/// single rich embed. Retries once on a 429 response using the server-provided <c>retry_after</c>
/// delay — Discord's own docs say rate limits must not be hardcoded, so this reads the actual
/// value from the response rather than using a fixed backoff constant.
/// </summary>
public sealed partial class DiscordNotificationChannel : INotificationChannel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _webhookUrl;
    private readonly ILogger<DiscordNotificationChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscordNotificationChannel"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Used to create the named HTTP client registered for Sentinel's outbound notifications.</param>
    /// <param name="webhookUrl">The admin-configured Discord webhook URL, exactly as Discord's own UI gives it.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public DiscordNotificationChannel(IHttpClientFactory httpClientFactory, Uri webhookUrl, ILogger<DiscordNotificationChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(webhookUrl);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _webhookUrl = webhookUrl;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ChannelName => "Discord";

    /// <inheritdoc />
    public string? LastFailureReason { get; private set; }

    /// <inheritdoc />
    public async Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            var client = _httpClientFactory.CreateClient("Sentinel.Notifications");
            var payload = BuildPayload(message);

            using var response = await client.PostAsJsonAsync(_webhookUrl, payload, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfterSeconds = await ReadRetryAfterSecondsAsync(response, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), cancellationToken).ConfigureAwait(false);

                using var retryResponse = await client.PostAsJsonAsync(_webhookUrl, payload, cancellationToken).ConfigureAwait(false);
                if (!retryResponse.IsSuccessStatusCode)
                {
                    LogDiscordSendFailed(_logger, (int)retryResponse.StatusCode);
                    LastFailureReason = $"HTTP {(int)retryResponse.StatusCode} {retryResponse.ReasonPhrase}";
                    return false;
                }

                LastFailureReason = null;
                return true;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogDiscordSendFailed(_logger, (int)response.StatusCode);
                LastFailureReason = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return false;
            }

            LastFailureReason = null;
            return true;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogDiscordSendException(_logger, ex);
            LastFailureReason = ex.Message;
            return false;
        }
#pragma warning restore CA1031
    }

    private static object BuildPayload(NotificationMessage message)
    {
        var embed = new Dictionary<string, object?>
        {
            ["title"] = message.Title,
            ["description"] = message.Body,
            ["color"] = SeverityToColor(message.Severity),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };

        // Defense in depth, matching the existing dashboard's identical https-only guard on
        // KnownIssueUrl (configPage.html's buildKnownIssueBlock) — IncidentUrl is always
        // dispatcher-constructed today, never from playback/session data, but this guard costs
        // nothing and guards against a future change accidentally allowing a non-https value in.
        if (Uri.TryCreate(message.IncidentUrl, UriKind.Absolute, out var incidentUri) &&
            incidentUri.Scheme == Uri.UriSchemeHttps)
        {
            embed["url"] = message.IncidentUrl;
        }

        return new { embeds = new[] { embed } };
    }

    // These specific hex values match the status-badge colors already used in the dashboard's own
    // CSS (configPage.html), for visual consistency between the plugin's own UI and its Discord
    // notifications — not an independent color choice.
    private static int SeverityToColor(string severity) => severity switch
    {
        "high" => 0xE05252,
        "medium" => 0xE0A030,
        _ => 0x4A90D9
    };

    private static async Task<double> ReadRetryAfterSecondsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const double DefaultRetryAfterSeconds = 1.0;

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("retry_after", out var retryAfterElement))
            {
                return retryAfterElement.GetDouble();
            }
        }
        catch (JsonException)
        {
        }

        return DefaultRetryAfterSeconds;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel: Discord notification send failed with status {StatusCode}")]
    private static partial void LogDiscordSendFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel: Discord notification send threw an exception")]
    private static partial void LogDiscordSendException(ILogger logger, Exception exception);
}
