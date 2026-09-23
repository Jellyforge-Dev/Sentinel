using System;
using Jellyfin.Plugin.Sentinel.Notifications;
using MimeKit;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Notifications;

public class EmailNotificationChannelTests
{
    private const string FromAddress = "sentinel@example.com";
    private const string ToAddress = "admin@example.com";

    [Fact]
    public void BuildMimeMessage_SetsFromToAndSubject()
    {
        var message = BuildMessage();

        var mimeMessage = EmailNotificationChannel.BuildMimeMessage(message, FromAddress, ToAddress);

        Assert.Contains(FromAddress, mimeMessage.From.ToString(), StringComparison.Ordinal);
        Assert.Contains(ToAddress, mimeMessage.To.ToString(), StringComparison.Ordinal);
        Assert.Equal(message.Title, mimeMessage.Subject);
    }

    [Fact]
    public void BuildMimeMessage_BodyContainsMessageBody()
    {
        var message = BuildMessage();

        var mimeMessage = EmailNotificationChannel.BuildMimeMessage(message, FromAddress, ToAddress);

        var bodyText = Assert.IsType<TextPart>(mimeMessage.Body).Text;
        Assert.Contains(message.Body, bodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMimeMessage_AppendsIncidentUrl_WhenHttps()
    {
        var message = new NotificationMessage
        {
            Title = "Test Incident",
            Body = "Something happened.",
            Severity = "high",
            IncidentUrl = "https://jellyfin.example.com/web/index.html#!/configurationpage?name=Sentinel"
        };

        var mimeMessage = EmailNotificationChannel.BuildMimeMessage(message, FromAddress, ToAddress);

        var bodyText = Assert.IsType<TextPart>(mimeMessage.Body).Text;
        Assert.Contains(message.IncidentUrl, bodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMimeMessage_OmitsIncidentUrl_WhenNotHttps()
    {
        var message = new NotificationMessage
        {
            Title = "Test Incident",
            Body = "Something happened.",
            Severity = "high",
            IncidentUrl = "http://example.com/incident/1"
        };

        var mimeMessage = EmailNotificationChannel.BuildMimeMessage(message, FromAddress, ToAddress);

        var bodyText = Assert.IsType<TextPart>(mimeMessage.Body).Text;
        Assert.DoesNotContain(message.IncidentUrl, bodyText, StringComparison.Ordinal);
    }

    private static NotificationMessage BuildMessage() => new()
    {
        Title = "Test Incident",
        Body = "Something happened.",
        Severity = "high",
        IncidentUrl = "https://jellyfin.example.com/web/index.html#!/configurationpage?name=Sentinel"
    };
}
