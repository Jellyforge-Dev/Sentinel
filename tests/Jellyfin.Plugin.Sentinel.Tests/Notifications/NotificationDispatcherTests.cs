using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Configuration;
using Jellyfin.Plugin.Sentinel.Domain;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Notifications;

public class NotificationDispatcherTests
{
    private static NotificationDispatcher CreateDispatcher() => new(
        new Mock<IHttpClientFactory>().Object,
        NullLoggerFactory.Instance,
        new LocalizationService(),
        NullLogger<NotificationDispatcher>.Instance);

    [Fact]
    public async Task DispatchAsync_DoesNotThrow_WhenPluginInstanceIsNull()
    {
        // Plugin.Instance is null in a test process (this codebase's own established pattern —
        // see SentinelControllerTests), so this exercises DispatchAsync's config-is-null
        // early-return/no-op path.
        var dispatcher = CreateDispatcher();
        var diagnosis = new Diagnosis
        {
            Code = "VIDEO_CODEC_UNSUPPORTED",
            Confidence = Confidence.Confirmed,
            Evidence = new[] { "evidence" }
        };
        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-1",
            ItemId = Guid.NewGuid().ToString(),
            Client = "Fire TV",
            DeviceName = "Living Room",
            UserName = "Alice",
            CreatedAtUtc = DateTime.UtcNow
        };

        var exception = await Record.ExceptionAsync(() => dispatcher.DispatchAsync(diagnosis, playbackEvent, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task DispatchMessageAsync_DoesNotThrow_WhenPluginInstanceIsNull()
    {
        // Mirrors DispatchAsync_DoesNotThrow_WhenPluginInstanceIsNull above: Plugin.Instance is
        // null in a test process, so this exercises DispatchMessageAsync's config-is-null
        // early-return/no-op path.
        var dispatcher = CreateDispatcher();
        var message = new NotificationMessage
        {
            Title = "Sentinel: 1 plugin update(s) available",
            Body = "Some Plugin v1.2.3",
            Severity = "low",
            IncidentUrl = string.Empty
        };

        var exception = await Record.ExceptionAsync(() => dispatcher.DispatchMessageAsync(message, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendTestNotificationAsync_ReturnsFalse_WhenPluginInstanceIsNull()
    {
        var dispatcher = CreateDispatcher();

        var result = await dispatcher.SendTestNotificationAsync("webhook", CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task DispatchAsync_Throws_WhenDiagnosisIsNull()
    {
        var dispatcher = CreateDispatcher();
        var playbackEvent = new PlaybackEvent
        {
            SessionId = "session-1",
            ItemId = Guid.NewGuid().ToString(),
            Client = "Fire TV",
            DeviceName = "Living Room",
            UserName = "Alice",
            CreatedAtUtc = DateTime.UtcNow
        };

        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync(null!, playbackEvent, CancellationToken.None));
    }

    [Fact]
    public async Task SendTestNotificationAsync_Throws_WhenChannelNameIsNull()
    {
        var dispatcher = CreateDispatcher();

        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.SendTestNotificationAsync(null!, CancellationToken.None));
    }

    // The tests below exercise BuildEnabledChannels/BuildChannelByName directly against a
    // constructed PluginConfiguration, bypassing the Plugin.Instance-is-null early return that
    // otherwise makes this codebase's most important wiring logic (which config field feeds which
    // channel, at which severity threshold) untestable in a plain unit-test process. This is the
    // exact class of bug (e.g. a copy-paste swap of DiscordMinSeverity for TelegramMinSeverity)
    // these tests catch that a passing build alone would not.

    [Fact]
    public void BuildChannelByName_ReturnsWebhookChannel_WhenEnabledAndUrlValid()
    {
        var dispatcher = CreateDispatcher();
        var config = new PluginConfiguration { WebhookUrl = "https://example.com/hook" };

        var channel = dispatcher.BuildChannelByName("webhook", config);

        Assert.NotNull(channel);
        Assert.Equal("Webhook", channel!.ChannelName);
    }

    [Fact]
    public void BuildChannelByName_ReturnsNull_WhenWebhookUrlIsInvalid()
    {
        var dispatcher = CreateDispatcher();
        var config = new PluginConfiguration { WebhookUrl = "not-a-url" };

        Assert.Null(dispatcher.BuildChannelByName("webhook", config));
    }

    [Fact]
    public void BuildChannelByName_ReturnsNull_ForUnknownChannelName()
    {
        var dispatcher = CreateDispatcher();
        var config = new PluginConfiguration();

        Assert.Null(dispatcher.BuildChannelByName("not-a-real-channel", config));
    }

    [Theory]
    [InlineData("webhook", "Webhook")]
    [InlineData("discord", "Discord")]
    [InlineData("telegram", "Telegram")]
    [InlineData("email", "Email")]
    public void BuildChannelByName_ReturnsTheCorrectChannelType_ForEachFullyConfiguredChannel(string channelName, string expectedChannelName)
    {
        var dispatcher = CreateDispatcher();
        var config = new PluginConfiguration
        {
            WebhookUrl = "https://example.com/webhook",
            DiscordWebhookUrl = "https://discord.com/api/webhooks/1/abc",
            TelegramBotToken = "bot-token",
            TelegramChatId = "chat-id",
            EmailSmtpHost = "smtp.example.com",
            EmailFromAddress = "sentinel@example.com",
            EmailToAddress = "admin@example.com"
        };

        var channel = dispatcher.BuildChannelByName(channelName, config);

        Assert.NotNull(channel);
        Assert.Equal(expectedChannelName, channel!.ChannelName);
    }

    [Fact]
    public void BuildEnabledChannels_PairsEachChannel_WithItsOwnMinSeverity_NotAnotherChannels()
    {
        // The load-bearing assertion: each channel's MinSeverity must come from that SAME
        // channel's own config field. Using a different value for every channel makes any
        // cross-wiring (e.g. Discord accidentally paired with Telegram's threshold) fail loudly.
        var dispatcher = CreateDispatcher();
        var config = new PluginConfiguration
        {
            WebhookEnabled = true,
            WebhookUrl = "https://example.com/webhook",
            WebhookMinSeverity = "low",

            DiscordEnabled = true,
            DiscordWebhookUrl = "https://discord.com/api/webhooks/1/abc",
            DiscordMinSeverity = "medium",

            TelegramEnabled = true,
            TelegramBotToken = "bot-token",
            TelegramChatId = "chat-id",
            TelegramMinSeverity = "high",

            EmailEnabled = true,
            EmailSmtpHost = "smtp.example.com",
            EmailFromAddress = "sentinel@example.com",
            EmailToAddress = "admin@example.com",
            EmailMinSeverity = "low"
        };

        var enabledChannels = dispatcher.BuildEnabledChannels(config).ToList();

        Assert.Equal(4, enabledChannels.Count);
        Assert.Equal("low", enabledChannels.Single(c => c.Channel.ChannelName == "Webhook").MinSeverity);
        Assert.Equal("medium", enabledChannels.Single(c => c.Channel.ChannelName == "Discord").MinSeverity);
        Assert.Equal("high", enabledChannels.Single(c => c.Channel.ChannelName == "Telegram").MinSeverity);
        Assert.Equal("low", enabledChannels.Single(c => c.Channel.ChannelName == "Email").MinSeverity);
    }

    [Fact]
    public void BuildEnabledChannels_SkipsDisabledChannels()
    {
        var dispatcher = CreateDispatcher();
        var config = new PluginConfiguration
        {
            WebhookEnabled = true,
            WebhookUrl = "https://example.com/webhook",

            // Discord/Telegram/Email are fully configured but NOT enabled — must not appear.
            DiscordWebhookUrl = "https://discord.com/api/webhooks/1/abc",
            TelegramBotToken = "bot-token",
            TelegramChatId = "chat-id",
            EmailSmtpHost = "smtp.example.com",
            EmailFromAddress = "sentinel@example.com",
            EmailToAddress = "admin@example.com"
        };

        var enabledChannels = dispatcher.BuildEnabledChannels(config).ToList();

        Assert.Single(enabledChannels);
        Assert.Equal("Webhook", enabledChannels[0].Channel.ChannelName);
    }

    [Fact]
    public void BuildEnabledChannels_SkipsEnabledChannel_WhenItsRequiredFieldsAreMissing()
    {
        var dispatcher = CreateDispatcher();
        var config = new PluginConfiguration
        {
            WebhookEnabled = true,
            WebhookUrl = string.Empty // enabled, but no URL configured yet
        };

        var enabledChannels = dispatcher.BuildEnabledChannels(config).ToList();

        Assert.Empty(enabledChannels);
    }
}
