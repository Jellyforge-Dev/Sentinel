using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Notifications;

public class WebhookNotificationChannelTests
{
    [Fact]
    public async Task SendAsync_ReturnsTrue_WhenResponseIsSuccessStatusCode()
    {
        var channel = BuildChannel(HttpStatusCode.OK, throwException: false);

        var result = await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task SendAsync_ReturnsFalse_WhenResponseIsNotSuccessStatusCode()
    {
        var channel = BuildChannel(HttpStatusCode.InternalServerError, throwException: false);

        var result = await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task SendAsync_ReturnsFalse_AndDoesNotThrow_WhenHttpClientThrows()
    {
        var channel = BuildChannel(HttpStatusCode.OK, throwException: true);

        var result = await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task SendAsync_PostsToTheConfiguredWebhookUrl()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var channel = BuildChannelFromHandler(handlerMock, new Uri("https://example.com/hook/abc"));

        await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(new Uri("https://example.com/hook/abc"), capturedRequest!.RequestUri);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
    }

    private static WebhookNotificationChannel BuildChannel(HttpStatusCode statusCode, bool throwException)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var setup = handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

        if (throwException)
        {
            setup.ThrowsAsync(new HttpRequestException("simulated network failure"));
        }
        else
        {
            setup.ReturnsAsync(new HttpResponseMessage(statusCode));
        }

        return BuildChannelFromHandler(handlerMock, new Uri("https://example.com/hook"));
    }

    private static WebhookNotificationChannel BuildChannelFromHandler(Mock<HttpMessageHandler> handlerMock, Uri webhookUrl)
    {
        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("Sentinel.Notifications")).Returns(httpClient);

        return new WebhookNotificationChannel(factoryMock.Object, webhookUrl, NullLogger<WebhookNotificationChannel>.Instance);
    }

    private static NotificationMessage BuildMessage() => new()
    {
        Title = "Test Incident",
        Body = "Something happened.",
        Severity = "high",
        IncidentUrl = "https://jellyfin.example.com/web/index.html#!/configurationpage?name=Sentinel"
    };
}
