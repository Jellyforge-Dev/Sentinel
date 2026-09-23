using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.Sentinel.Localization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sentinel.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
[SuppressMessage("Design", "CA1056:Uri properties should not be strings", Justification = "These are free-form strings entered via the dashboard's config UI, not typed Uri values; NotificationDispatcher parses them with Uri.TryCreate before use, matching NotificationMessage.IncidentUrl's identical suppression.")]
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the language Sentinel renders its dashboard and diagnosis explanations in.
    /// Independent of Jellyfin's own per-user display language — see
    /// <see cref="SupportedLanguage"/>'s remarks.
    /// </summary>
    public SupportedLanguage Language { get; set; } = SupportedLanguage.En;

    /// <summary>
    /// Gets or sets the Telegram bot token used to send notifications, if Telegram notifications are enabled.
    /// </summary>
    public string TelegramBotToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Telegram chat ID notifications are sent to, if Telegram notifications are enabled.
    /// </summary>
    public string TelegramChatId { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the Telegram notification channel is enabled.</summary>
    public bool TelegramEnabled { get; set; }

    /// <summary>Gets or sets the minimum severity that triggers a Telegram notification.</summary>
    public string TelegramMinSeverity { get; set; } = "low";

    /// <summary>Gets or sets the SMTP server host for email notifications.</summary>
    public string EmailSmtpHost { get; set; } = string.Empty;

    /// <summary>Gets or sets the SMTP server port for email notifications.</summary>
    public int EmailSmtpPort { get; set; } = 587;

    /// <summary>Gets or sets the SMTP username, if the server requires authentication.</summary>
    public string EmailSmtpUsername { get; set; } = string.Empty;

    /// <summary>Gets or sets the SMTP password, if the server requires authentication.</summary>
    public string EmailSmtpPassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the "From" address for outgoing notification emails.</summary>
    public string EmailFromAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets the "To" address notification emails are sent to.</summary>
    public string EmailToAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the email notification channel is enabled.</summary>
    public bool EmailEnabled { get; set; }

    /// <summary>Gets or sets the minimum severity that triggers an email notification.</summary>
    public string EmailMinSeverity { get; set; } = "low";

    /// <summary>Gets or sets whether the generic webhook notification channel is enabled.</summary>
    public bool WebhookEnabled { get; set; }

    /// <summary>Gets or sets the destination URL for the generic webhook notification channel.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum severity ("low"/"medium"/"high") that triggers a webhook notification.</summary>
    public string WebhookMinSeverity { get; set; } = "low";

    /// <summary>Gets or sets whether the Discord notification channel is enabled.</summary>
    public bool DiscordEnabled { get; set; }

    /// <summary>Gets or sets the Discord webhook URL notifications are sent to.</summary>
    public string DiscordWebhookUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum severity that triggers a Discord notification.</summary>
    public string DiscordMinSeverity { get; set; } = "low";
}
