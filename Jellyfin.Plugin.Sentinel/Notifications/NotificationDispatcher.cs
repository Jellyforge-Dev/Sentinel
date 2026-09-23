using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Configuration;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Localization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// Builds a <see cref="NotificationMessage"/> from a diagnosis and sends it to every enabled,
/// severity-eligible channel. Callers are responsible for only invoking <see cref="DispatchAsync"/>
/// once per new-or-reopened incident — see <see cref="Persistence.IncidentUpsertResult"/> — never per raw
/// diagnosis or per plain occurrence-count increment.
/// </summary>
public sealed partial class NotificationDispatcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LocalizationService _localizationService;
    private readonly ILogger<NotificationDispatcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationDispatcher"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Used to construct HTTP-based channels.</param>
    /// <param name="loggerFactory">Used to construct each channel's own typed logger.</param>
    /// <param name="localizationService">Used to translate the diagnosis code into an explanation.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public NotificationDispatcher(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, LocalizationService localizationService, ILogger<NotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(localizationService);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _localizationService = localizationService;
        _logger = logger;
    }

    /// <summary>
    /// Builds and sends a notification for a new-or-reopened incident to every enabled,
    /// severity-eligible channel. Never throws — every failure is logged and swallowed, since a
    /// notification-delivery failure must never affect the playback-collection path that
    /// triggers it.
    /// </summary>
    /// <param name="diagnosis">The diagnosis that triggered the incident.</param>
    /// <param name="playbackEvent">The playback event the diagnosis was produced from.</param>
    /// <param name="cancellationToken">Used to cancel the dispatch.</param>
    public async Task DispatchAsync(Diagnosis diagnosis, PlaybackEvent playbackEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        ArgumentNullException.ThrowIfNull(playbackEvent);

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return;
            }

            var severity = NotificationSeverityMapper.FromConfidence(diagnosis.Confidence);
            var explanation = _localizationService.Translate($"{diagnosis.Code}_EXPLANATION", config.Language);

            var message = new NotificationMessage
            {
                Title = $"Sentinel: {diagnosis.Code}",
                Body = BuildBody(explanation, playbackEvent),
                Severity = severity,

                // A real deep link back to the dashboard needs Jellyfin's server base-URL API,
                // which this plan has not verified yet. Every channel already handles an
                // empty/non-https IncidentUrl by simply omitting it (established in Tasks
                // C3/C4), so this is a known, deliberate simplification, not an oversight.
                IncidentUrl = string.Empty
            };

            foreach (var (channel, minSeverity) in BuildEnabledChannels(config))
            {
                if (!NotificationSeverityMapper.MeetsThreshold(severity, minSeverity))
                {
                    continue;
                }

                await channel.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogDispatchFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Sends a fixed test message through the named channel (<c>"webhook"</c>/<c>"discord"</c>/
    /// <c>"telegram"</c>/<c>"email"</c>), regardless of that channel's enabled flag or severity
    /// threshold — used by the dashboard's per-channel "Send test" button.
    /// </summary>
    /// <param name="channelName">The channel to test.</param>
    /// <param name="cancellationToken">Used to cancel the send.</param>
    /// <returns>True if the test message was sent successfully; false if the channel isn't configured or the send failed.</returns>
    public async Task<bool> SendTestNotificationAsync(string channelName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelName);

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return false;
        }

        var channel = BuildChannelByName(channelName, config);
        if (channel is null)
        {
            return false;
        }

        var testMessage = new NotificationMessage
        {
            Title = "Sentinel test notification",
            Body = "This is a test notification from Jellyfin Sentinel. If you received this, the channel is configured correctly.",
            Severity = "low",
            IncidentUrl = string.Empty
        };

        return await channel.SendAsync(testMessage, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildBody(string explanation, PlaybackEvent playbackEvent)
    {
        var body = explanation + "\n\nClient: " + playbackEvent.Client + " / " + playbackEvent.DeviceName;
        if (!string.IsNullOrEmpty(playbackEvent.UserName))
        {
            body += "\nUser: " + playbackEvent.UserName;
        }

        return body;
    }

    /// <summary>
    /// Builds every enabled channel paired with its configured minimum severity, reading directly
    /// from a supplied <see cref="PluginConfiguration"/> rather than <see cref="Plugin.Instance"/>.
    /// Internal (not private) specifically so tests can exercise the enable-flag/min-severity
    /// wiring directly — e.g. that <c>DiscordEnabled</c> pairs with <c>DiscordMinSeverity</c> and
    /// never with another channel's threshold — without needing a live <see cref="Plugin.Instance"/>,
    /// which is always null in a test process.
    /// </summary>
    internal IEnumerable<(INotificationChannel Channel, string MinSeverity)> BuildEnabledChannels(PluginConfiguration config)
    {
        if (config.WebhookEnabled)
        {
            var channel = BuildChannelByName("webhook", config);
            if (channel is not null)
            {
                yield return (channel, config.WebhookMinSeverity);
            }
        }

        if (config.DiscordEnabled)
        {
            var channel = BuildChannelByName("discord", config);
            if (channel is not null)
            {
                yield return (channel, config.DiscordMinSeverity);
            }
        }

        if (config.TelegramEnabled)
        {
            var channel = BuildChannelByName("telegram", config);
            if (channel is not null)
            {
                yield return (channel, config.TelegramMinSeverity);
            }
        }

        if (config.EmailEnabled)
        {
            var channel = BuildChannelByName("email", config);
            if (channel is not null)
            {
                yield return (channel, config.EmailMinSeverity);
            }
        }
    }

    /// <summary>
    /// Constructs the named channel from a supplied <see cref="PluginConfiguration"/>, or null if
    /// that channel's required fields aren't populated. Internal so tests can verify each channel
    /// name maps to the correct config fields directly.
    /// </summary>
    internal INotificationChannel? BuildChannelByName(string channelName, PluginConfiguration config) => channelName switch
    {
        "webhook" when Uri.TryCreate(config.WebhookUrl, UriKind.Absolute, out var webhookUri) =>
            new WebhookNotificationChannel(_httpClientFactory, webhookUri, _loggerFactory.CreateLogger<WebhookNotificationChannel>()),
        "discord" when Uri.TryCreate(config.DiscordWebhookUrl, UriKind.Absolute, out var discordUri) =>
            new DiscordNotificationChannel(_httpClientFactory, discordUri, _loggerFactory.CreateLogger<DiscordNotificationChannel>()),
        "telegram" when !string.IsNullOrEmpty(config.TelegramBotToken) && !string.IsNullOrEmpty(config.TelegramChatId) =>
            new TelegramNotificationChannel(_httpClientFactory, config.TelegramBotToken, config.TelegramChatId, _loggerFactory.CreateLogger<TelegramNotificationChannel>()),
        "email" when !string.IsNullOrEmpty(config.EmailSmtpHost) && !string.IsNullOrEmpty(config.EmailFromAddress) && !string.IsNullOrEmpty(config.EmailToAddress) =>
            new EmailNotificationChannel(config.EmailSmtpHost, config.EmailSmtpPort, config.EmailSmtpUsername, config.EmailSmtpPassword, config.EmailFromAddress, config.EmailToAddress, _loggerFactory.CreateLogger<EmailNotificationChannel>()),
        _ => null
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Sentinel: notification dispatch failed")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception);
}
