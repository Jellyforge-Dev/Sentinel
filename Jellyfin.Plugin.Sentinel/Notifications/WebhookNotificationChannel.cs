using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// Sends a <see cref="NotificationMessage"/> as a JSON POST to an admin-configured webhook URL.
/// </summary>
public sealed partial class WebhookNotificationChannel : INotificationChannel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _webhookUrl;
    private readonly ILogger<WebhookNotificationChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookNotificationChannel"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Used to create the named HTTP client registered for Sentinel's outbound notifications.</param>
    /// <param name="webhookUrl">The admin-configured destination URL.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public WebhookNotificationChannel(IHttpClientFactory httpClientFactory, Uri webhookUrl, ILogger<WebhookNotificationChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(webhookUrl);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _webhookUrl = webhookUrl;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ChannelName => "Webhook";

    /// <inheritdoc />
    public string? LastFailureReason { get; private set; }

    /// <inheritdoc />
    public async Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            var client = _httpClientFactory.CreateClient("Sentinel.Notifications");
            using var response = await client.PostAsJsonAsync(_webhookUrl, message, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogWebhookSendFailed(_logger, (int)response.StatusCode);
                LastFailureReason = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return false;
            }

            LastFailureReason = null;
            return true;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogWebhookSendException(_logger, ex);
            LastFailureReason = ex.Message;
            return false;
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel: webhook notification send failed with status {StatusCode}")]
    private static partial void LogWebhookSendFailed(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel: webhook notification send threw an exception")]
    private static partial void LogWebhookSendException(ILogger logger, Exception exception);
}
