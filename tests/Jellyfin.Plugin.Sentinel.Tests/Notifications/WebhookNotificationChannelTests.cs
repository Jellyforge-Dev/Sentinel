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

        var channel = BuildChannelFromHandler(handlerMock, new Uri("https://example.com/hook/abc"), string.Empty);

        await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(new Uri("https://example.com/hook/abc"), capturedRequest!.RequestUri);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
    }

    [Fact]
    public async Task SendAsync_AddsSecretHeader_WhenSecretIsConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var channel = BuildChannelFromHandler(handlerMock, new Uri("https://example.com/hook/abc"), "top-secret");

        await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.TryGetValues("X-Sentinel-Secret", out var values));
        Assert.Equal("top-secret", Assert.Single(values!));
    }

    [Fact]
    public async Task SendAsync_OmitsSecretHeader_WhenSecretIsEmpty()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var channel = BuildChannelFromHandler(handlerMock, new Uri("https://example.com/hook/abc"), string.Empty);

        await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.False(capturedRequest!.Headers.Contains("X-Sentinel-Secret"));
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

        return BuildChannelFromHandler(handlerMock, new Uri("https://example.com/hook"), string.Empty);
    }

    private static WebhookNotificationChannel BuildChannelFromHandler(Mock<HttpMessageHandler> handlerMock, Uri webhookUrl, string secret)
    {
        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("Sentinel.Notifications")).Returns(httpClient);

        return new WebhookNotificationChannel(factoryMock.Object, webhookUrl, secret, NullLogger<WebhookNotificationChannel>.Instance);
    }

    private static NotificationMessage BuildMessage() => new()
    {
        Title = "Test Incident",
        Body = "Something happened.",
        Severity = "critical",
        IncidentUrl = "https://jellyfin.example.com/web/index.html#!/configurationpage?name=Sentinel"
    };
}
