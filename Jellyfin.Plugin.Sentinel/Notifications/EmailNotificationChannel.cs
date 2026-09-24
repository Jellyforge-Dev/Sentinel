using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Notifications;

/// <summary>
/// Sends a <see cref="NotificationMessage"/> as a plain-text email via an admin-configured SMTP
/// server, using MailKit (the maintained replacement for the obsolete
/// <see cref="System.Net.Mail.SmtpClient"/>).
/// </summary>
public sealed partial class EmailNotificationChannel : INotificationChannel
{
    private readonly string _smtpHost;
    private readonly int _smtpPort;
    private readonly string _smtpUsername;
    private readonly string _smtpPassword;
    private readonly string _fromAddress;
    private readonly string _toAddress;
    private readonly ILogger<EmailNotificationChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailNotificationChannel"/> class.
    /// </summary>
    /// <param name="smtpHost">The SMTP server host.</param>
    /// <param name="smtpPort">The SMTP server port.</param>
    /// <param name="smtpUsername">The SMTP username, or empty if the server requires no authentication.</param>
    /// <param name="smtpPassword">The SMTP password, or empty if the server requires no authentication.</param>
    /// <param name="fromAddress">The "From" address for outgoing emails.</param>
    /// <param name="toAddress">The "To" address for outgoing emails.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public EmailNotificationChannel(string smtpHost, int smtpPort, string smtpUsername, string smtpPassword, string fromAddress, string toAddress, ILogger<EmailNotificationChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(smtpHost);
        ArgumentNullException.ThrowIfNull(smtpUsername);
        ArgumentNullException.ThrowIfNull(smtpPassword);
        ArgumentNullException.ThrowIfNull(fromAddress);
        ArgumentNullException.ThrowIfNull(toAddress);
        ArgumentNullException.ThrowIfNull(logger);
        _smtpHost = smtpHost;
        _smtpPort = smtpPort;
        _smtpUsername = smtpUsername;
        _smtpPassword = smtpPassword;
        _fromAddress = fromAddress;
        _toAddress = toAddress;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ChannelName => "Email";

    /// <inheritdoc />
    public string? LastFailureReason { get; private set; }

    /// <inheritdoc />
    public async Task<bool> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            using var mimeMessage = BuildMimeMessage(message, _fromAddress, _toAddress);

            using var client = new SmtpClient();

            // Deliberately not SecureSocketOptions.Auto: Auto silently falls back to an
            // unencrypted connection if the server doesn't advertise STARTTLS (or a
            // man-in-the-middle strips the advertisement), which would then submit the SMTP
            // credentials below in cleartext. StartTls/SslOnConnect both fail the connection
            // outright instead of downgrading, so a misconfigured or hostile server can never
            // silently capture credentials this channel sends.
            var secureSocketOptions = _smtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(_smtpHost, _smtpPort, secureSocketOptions, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_smtpUsername))
            {
                await client.AuthenticateAsync(_smtpUsername, _smtpPassword, cancellationToken).ConfigureAwait(false);
            }

            await client.SendAsync(mimeMessage, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

            LastFailureReason = null;
            return true;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogEmailSendException(_logger, ex);
            LastFailureReason = ex.Message;
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Builds the outgoing MIME message from a notification, appending the incident URL to the
    /// body when it's a valid https link. Internal-visible logic factored out so it's testable
    /// without a real SMTP connection.
    /// </summary>
    internal static MimeMessage BuildMimeMessage(NotificationMessage message, string fromAddress, string toAddress)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(MailboxAddress.Parse(fromAddress));
        mimeMessage.To.Add(MailboxAddress.Parse(toAddress));
        mimeMessage.Subject = message.Title;

        var bodyText = message.Body;
        if (Uri.TryCreate(message.IncidentUrl, UriKind.Absolute, out var incidentUri) &&
            incidentUri.Scheme == Uri.UriSchemeHttps)
        {
            bodyText += "\n\n" + message.IncidentUrl;
        }

        mimeMessage.Body = new TextPart("plain") { Text = bodyText };
        return mimeMessage;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel: email notification send threw an exception")]
    private static partial void LogEmailSendException(ILogger logger, Exception exception);
}
