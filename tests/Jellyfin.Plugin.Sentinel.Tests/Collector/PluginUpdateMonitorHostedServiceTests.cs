using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Collector;
using Jellyfin.Plugin.Sentinel.Localization;
using Jellyfin.Plugin.Sentinel.Notifications;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Common;
using MediaBrowser.Common.Updates;
using MediaBrowser.Model.Updates;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Collector;

public class PluginUpdateMonitorHostedServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;
    private readonly NotifiedPluginUpdateRepository _notifiedPluginUpdateRepository;

    public PluginUpdateMonitorHostedServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-plugin-update-monitor-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
        _notifiedPluginUpdateRepository = new NotifiedPluginUpdateRepository(_database);
    }

    private static NotificationDispatcher CreateNotificationDispatcher() => new(
        new Mock<IHttpClientFactory>().Object,
        NullLoggerFactory.Instance,
        new LocalizationService(),
        NullLogger<NotificationDispatcher>.Instance);

    private PluginUpdateMonitorHostedService CreateService(
        Mock<IInstallationManager> installationManagerMock,
        Mock<IApplicationHost> applicationHostMock) =>
        new(
            installationManagerMock.Object,
            applicationHostMock.Object,
            _notifiedPluginUpdateRepository,
            CreateNotificationDispatcher(),
            NullLogger<PluginUpdateMonitorHostedService>.Instance);

    [Fact]
    public async Task CheckForPluginUpdatesAsync_NotifiesOnce_ForANewlyAvailableUpdate()
    {
        var pluginId = Guid.NewGuid();
        var installationManagerMock = new Mock<IInstallationManager>();
        installationManagerMock
            .Setup(m => m.GetAvailablePluginUpdates(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstallationInfo>
            {
                new() { Id = pluginId, Name = "Some Plugin", Version = new Version(1, 2, 3) }
            });
        var applicationHostMock = new Mock<IApplicationHost>();

        var service = CreateService(installationManagerMock, applicationHostMock);

        await service.CheckForPluginUpdatesAsync(CancellationToken.None);

        Assert.True(_notifiedPluginUpdateRepository.HasBeenNotified(pluginId, "1.2.3"));
    }

    [Fact]
    public async Task CheckForPluginUpdatesAsync_DoesNotReNotify_ForAnAlreadyNotifiedUpdate()
    {
        var pluginId = Guid.NewGuid();
        var installationManagerMock = new Mock<IInstallationManager>();
        installationManagerMock
            .Setup(m => m.GetAvailablePluginUpdates(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstallationInfo>
            {
                new() { Id = pluginId, Name = "Some Plugin", Version = new Version(1, 2, 3) }
            });
        var applicationHostMock = new Mock<IApplicationHost>();

        var service = CreateService(installationManagerMock, applicationHostMock);

        await service.CheckForPluginUpdatesAsync(CancellationToken.None);
        Assert.True(_notifiedPluginUpdateRepository.HasBeenNotified(pluginId, "1.2.3"));

        await service.CheckForPluginUpdatesAsync(CancellationToken.None);

        Assert.Equal(1, CountNotifiedRows(pluginId, "1.2.3"));
    }

    [Fact]
    public async Task OnHasPendingRestartChanged_FiresExactlyOnce_OnFalseToTrueTransition()
    {
        var installationManagerMock = new Mock<IInstallationManager>();
        var applicationHostMock = new Mock<IApplicationHost>();
        var hasPendingRestart = false;
        applicationHostMock.SetupGet(m => m.HasPendingRestart).Returns(() => hasPendingRestart);

        var service = CreateService(installationManagerMock, applicationHostMock);

        await service.StartAsync(CancellationToken.None);

        hasPendingRestart = true;
        applicationHostMock.Raise(m => m.HasPendingRestartChanged += null, EventArgs.Empty);

        Assert.Equal(1, service.RestartPendingDispatchCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnHasPendingRestartChanged_DoesNotFireASecondTime_WhenAlreadyTrueAndEventRaisedAgain()
    {
        var installationManagerMock = new Mock<IInstallationManager>();
        var applicationHostMock = new Mock<IApplicationHost>();
        var hasPendingRestart = false;
        applicationHostMock.SetupGet(m => m.HasPendingRestart).Returns(() => hasPendingRestart);

        var service = CreateService(installationManagerMock, applicationHostMock);

        await service.StartAsync(CancellationToken.None);

        hasPendingRestart = true;
        applicationHostMock.Raise(m => m.HasPendingRestartChanged += null, EventArgs.Empty);

        // Jellyfin raises the event again while HasPendingRestart is still true (e.g. another
        // unrelated pending-restart-causing change) — this must not fire a second notification.
        applicationHostMock.Raise(m => m.HasPendingRestartChanged += null, EventArgs.Empty);

        Assert.Equal(1, service.RestartPendingDispatchCount);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatchRestartPendingNotificationAsync_DoesNotThrow_WhenCalledDirectly()
    {
        var installationManagerMock = new Mock<IInstallationManager>();
        var applicationHostMock = new Mock<IApplicationHost>();
        var service = CreateService(installationManagerMock, applicationHostMock);

        var exception = await Record.ExceptionAsync(() => service.DispatchRestartPendingNotificationAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public void BuildRestartPendingBody_MentionsRecentUpdate_WhenOneWasNotifiedRecently()
    {
        var body = PluginUpdateMonitorHostedService.BuildRestartPendingBody(hasRecentUpdate: true);

        Assert.Contains("A server restart is pending.", body, StringComparison.Ordinal);
        Assert.Contains("recently installed plugin update", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRestartPendingBody_OmitsRecentUpdateMention_WhenNoneWasNotifiedRecently()
    {
        var body = PluginUpdateMonitorHostedService.BuildRestartPendingBody(hasRecentUpdate: false);

        Assert.Equal("A server restart is pending.", body);
    }

    [Fact]
    public void HasRecentNotification_ReflectsWhetherMarkNotifiedWasCalledRecently()
    {
        var pluginId = Guid.NewGuid();

        Assert.False(_notifiedPluginUpdateRepository.HasRecentNotification(TimeSpan.FromMinutes(10)));

        _notifiedPluginUpdateRepository.MarkNotified(pluginId, "1.0.0");

        Assert.True(_notifiedPluginUpdateRepository.HasRecentNotification(TimeSpan.FromMinutes(10)));
    }

    private long CountNotifiedRows(Guid pluginId, string version)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM NotifiedPluginUpdate WHERE PluginId = $pluginId AND Version = $version;";
        command.Parameters.AddWithValue("$pluginId", pluginId.ToString());
        command.Parameters.AddWithValue("$version", version);
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
