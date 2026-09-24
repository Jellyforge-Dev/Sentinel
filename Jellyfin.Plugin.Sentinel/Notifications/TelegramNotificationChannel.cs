using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// Sends a <see cref="NotificationMessage"/> to an admin-configured Telegram chat via the Bot
/// API's <c>sendMessage</c> method. Retries once on a 429 response using the server-provided
/// retry delay, matching <see cref="DiscordNotificationChannel"/>'s identical policy.
/// </summary>
public sealed partial class TelegramNotificationChannel : INotificationChannel
{
    private const int MaxTelegramTextLength = 4096;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly ILogger<TelegramNotificationChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramNotificationChannel"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Used to create the named HTTP client registered for Sentinel's outbound notifications.</param>
    /// <param name="botToken">The admin-configured Telegram bot token.</param>
    /// <param name="chatId">The admin-configured destination chat ID.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public TelegramNotificationChannel(IHttpClientFactory httpClientFactory, string botToken, string chatId, ILogger<TelegramNotificationChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(botToken);
        ArgumentNullException.ThrowIfNull(chatId);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _botToken = botToken;
        _chatId = chatId;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ChannelName => "Telegram";

    /// <inheritdoc />
    public string? LastFailureReason { get; private set; }

    /// <inheritdoc />
    public async Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            var client = _httpClientFactory.CreateClient("Sentinel.Notifications");
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new { chat_id = _chatId, text = BuildText(message) };

            using var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfterSeconds = await ReadRetryAfterSecondsAsync(response, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), cancellationToken).ConfigureAwait(false);

                using var retryResponse = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
                if (!retryResponse.IsSuccessStatusCode)
                {
                    LogTelegramSendFailed(_logger, (int)retryResponse.StatusCode);
                    LastFailureReason = $"HTTP {(int)retryResponse.StatusCode} {retryResponse.ReasonPhrase}";
                    return false;
                }

                LastFailureReason = null;
                return true;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogTelegramSendFailed(_logger, (int)response.StatusCode);
                LastFailureReason = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return false;
            }

            LastFailureReason = null;
            return true;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogTelegramSendException(_logger, ex);
            LastFailureReason = ex.Message;
            return false;
        }
#pragma warning restore CA1031
    }

    private static string BuildText(NotificationMessage message)
    {
        var text = message.Title + "\n\n" + message.Body;

        if (Uri.TryCreate(message.IncidentUrl, UriKind.Absolute, out var incidentUri) &&
            incidentUri.Scheme == Uri.UriSchemeHttps)
        {
            text += "\n" + message.IncidentUrl;
        }

        return text.Length > MaxTelegramTextLength
            ? string.Concat(text.AsSpan(0, MaxTelegramTextLength - 1), "…")
            : text;
    }

    private static async Task<double> ReadRetryAfterSecondsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const double DefaultRetryAfterSeconds = 1.0;

        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            // Telegram's documented shape nests retry_after under "parameters"; fall back to a
            // top-level field in case that assumption is wrong, then to a fixed default if
            // neither is present — see the brief's note on why this couldn't be fully confirmed
            // against the live docs this session.
            if (root.TryGetProperty("parameters", out var parameters) &&
                parameters.TryGetProperty("retry_after", out var nestedRetryAfter))
            {
                return nestedRetryAfter.GetDouble();
            }

            if (root.TryGetProperty("retry_after", out var topLevelRetryAfter))
            {
                return topLevelRetryAfter.GetDouble();
            }
        }
        catch (JsonException)
        {
        }

        return DefaultRetryAfterSeconds;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel: Telegram notification send failed with status {StatusCode}")]
    private static partial void LogTelegramSendFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel: Telegram notification send threw an exception")]
    private static partial void LogTelegramSendException(ILogger logger, Exception exception);
}
