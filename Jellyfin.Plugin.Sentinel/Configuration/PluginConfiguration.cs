using Jellyfin.Plugin.Sentinel.Localization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sentinel.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
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
}
