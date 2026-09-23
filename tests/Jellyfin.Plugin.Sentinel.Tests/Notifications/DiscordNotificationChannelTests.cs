using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Notifications;

public class DiscordNotificationChannelTests
{
    [Fact]
    public async Task SendAsync_ReturnsTrue_WhenResponseIsSuccessStatusCode()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var channel = BuildChannelFromHandler(handlerMock);

        var result = await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task SendAsync_ReturnsFalse_WhenResponseIsNotSuccessAndNotRateLimited()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var setup = handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        var callCount = 0;
        setup.ReturnsAsync(() =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var channel = BuildChannelFromHandler(handlerMock);

        var result = await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task SendAsync_RetriesOnce_AndReturnsTrue_WhenRateLimitedThenSucceeds()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var setup = handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        var callCount = 0;
        setup.ReturnsAsync(() =>
        {
            callCount++;
            return callCount == 1
                ? BuildRateLimitedResponse()
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var channel = BuildChannelFromHandler(handlerMock);

        var result = await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.True(result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task SendAsync_ReturnsFalse_WhenRateLimitedTwice()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        var setup = handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        var callCount = 0;
        setup.ReturnsAsync(() =>
        {
            callCount++;
            return callCount == 1
                ? BuildRateLimitedResponse()
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        var channel = BuildChannelFromHandler(handlerMock);

        var result = await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.False(result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task SendAsync_ReturnsFalse_AndDoesNotThrow_WhenHttpClientThrows()
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("simulated network failure"));

        var channel = BuildChannelFromHandler(handlerMock);

        var result = await channel.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task SendAsync_BuildsAnEmbedWithTitleDescriptionAndColor()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var channel = BuildChannelFromHandler(handlerMock);
        var message = BuildMessage();

        await channel.SendAsync(message, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var embeds = document.RootElement.GetProperty("embeds");

        Assert.Equal(1, embeds.GetArrayLength());
        var embed = embeds[0];
        Assert.Equal(message.Title, embed.GetProperty("title").GetString());
        Assert.Equal(message.Body, embed.GetProperty("description").GetString());
        Assert.Equal(0xE05252, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public async Task SendAsync_OmitsEmbedUrl_WhenIncidentUrlIsNotHttps()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var channel = BuildChannelFromHandler(handlerMock);
        var message = new NotificationMessage
        {
            Title = "Test Incident",
            Body = "Something happened.",
            Severity = "high",
            IncidentUrl = "http://example.com/incident/1"
        };

        await channel.SendAsync(message, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var embed = document.RootElement.GetProperty("embeds")[0];

        Assert.False(embed.TryGetProperty("url", out _));
    }

    private static HttpResponseMessage BuildRateLimitedResponse() => new(HttpStatusCode.TooManyRequests)
    {
        Content = JsonContent.Create(new { retry_after = 0.01, global = false })
    };

    private static DiscordNotificationChannel BuildChannelFromHandler(Mock<HttpMessageHandler> handlerMock)
    {
        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("Sentinel.Notifications")).Returns(httpClient);

        return new DiscordNotificationChannel(factoryMock.Object, new Uri("https://discord.com/api/webhooks/123/abc"), NullLogger<DiscordNotificationChannel>.Instance);
    }

    private static NotificationMessage BuildMessage() => new()
    {
        Title = "Test Incident",
        Body = "Something happened.",
        Severity = "high",
        IncidentUrl = "https://jellyfin.example.com/web/index.html#!/configurationpage?name=Sentinel"
    };
}
