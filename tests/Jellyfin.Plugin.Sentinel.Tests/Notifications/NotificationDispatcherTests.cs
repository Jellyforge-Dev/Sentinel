using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    public async Task SendTestNotificationAsync_ReturnsFalse_WhenPluginInstanceIsNull()
    {
        var dispatcher = CreateDispatcher();

        var result = await dispatcher.SendTestNotificationAsync("webhook", CancellationToken.None);

        Assert.False(result);
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
}
