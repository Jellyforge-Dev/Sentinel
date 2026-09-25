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

public class TelegramNotificationChannelTests
{
    private const string FakeBotToken = "123456:FAKE-BOT-TOKEN-abcDEF";
    private const string FakeChatId = "-1001234567890";

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
    public async Task SendAsync_PostsToTheCorrectUrl_WithChatIdAndText()
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
        Assert.Equal($"https://api.telegram.org/bot{FakeBotToken}/sendMessage", capturedRequest!.RequestUri!.ToString());

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(FakeChatId, document.RootElement.GetProperty("chat_id").GetString());
        var text = document.RootElement.GetProperty("text").GetString();
        Assert.Contains(message.Title, text, StringComparison.Ordinal);
        Assert.Contains(message.Body, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_TruncatesText_WhenLongerThan4096Characters()
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
            Body = new string('x', 5000),
            Severity = "critical",
            IncidentUrl = "https://jellyfin.example.com/web/index.html#!/configurationpage?name=Sentinel"
        };

        await channel.SendAsync(message, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var text = document.RootElement.GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.Equal(4096, text!.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_OmitsIncidentUrl_WhenNotHttps()
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
            Severity = "critical",
            IncidentUrl = "http://example.com/incident/1"
        };

        await channel.SendAsync(message, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var text = document.RootElement.GetProperty("text").GetString();

        Assert.DoesNotContain(message.IncidentUrl, text, StringComparison.Ordinal);
    }

    private static HttpResponseMessage BuildRateLimitedResponse() => new(HttpStatusCode.TooManyRequests)
    {
        Content = JsonContent.Create(new
        {
            ok = false,
            error_code = 429,
            description = "Too Many Requests: retry after 0",
            parameters = new { retry_after = 0.01 }
        })
    };

    private static TelegramNotificationChannel BuildChannelFromHandler(Mock<HttpMessageHandler> handlerMock)
    {
        var httpClient = new HttpClient(handlerMock.Object);
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("Sentinel.Notifications")).Returns(httpClient);

        return new TelegramNotificationChannel(factoryMock.Object, FakeBotToken, FakeChatId, NullLogger<TelegramNotificationChannel>.Instance);
    }

    private static NotificationMessage BuildMessage() => new()
    {
        Title = "Test Incident",
        Body = "Something happened.",
        Severity = "critical",
        IncidentUrl = "https://jellyfin.example.com/web/index.html#!/configurationpage?name=Sentinel"
    };
}
