using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sentinel.Notifications;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Common;
using MediaBrowser.Common.Updates;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel.Collector;

/// <summary>
/// Periodically checks for available plugin updates and watches for a pending server restart,
/// notifying at most once per distinct plugin+version and at most once per false-to-true restart
/// transition. "A plugin update is available" and "a restart is pending" are independent signals
/// — <see cref="IApplicationHost.HasPendingRestart"/> can be true for reasons unrelated to plugins,
/// and an available update does not by itself set it — so the two notifications are always built
/// and evaluated separately, never conflated.
/// </summary>
public sealed partial class PluginUpdateMonitorHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RecentUpdateWindow = TimeSpan.FromMinutes(10);

    private readonly IInstallationManager _installationManager;
    private readonly IApplicationHost _applicationHost;
    private readonly NotifiedPluginUpdateRepository _notifiedPluginUpdateRepository;
    private readonly NotificationDispatcher _notificationDispatcher;
    private readonly ILogger<PluginUpdateMonitorHostedService> _logger;
    private Timer? _timer;
    private bool _lastKnownPendingRestart;

    /// <summary>
    /// Gets how many times the false-to-true restart-pending transition has actually triggered a
    /// dispatch. Internal (not private) purely so tests can verify the once-per-transition gating
    /// in <see cref="OnHasPendingRestartChanged"/> without depending on the async dispatch
    /// completing, or on a real <see cref="NotificationDispatcher"/> delivery (which safely
    /// no-ops in a test process, since <c>Plugin.Instance</c> is always null there).
    /// </summary>
    internal int RestartPendingDispatchCount { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginUpdateMonitorHostedService"/> class.
    /// </summary>
    /// <param name="installationManager">Used to check for available plugin updates. Already registered by Jellyfin's own host — do not register a second instance.</param>
    /// <param name="applicationHost">Used to observe pending-restart state. Already registered by Jellyfin's own host, same as above.</param>
    /// <param name="notifiedPluginUpdateRepository">Tracks which plugin+version pairs have already been notified about.</param>
    /// <param name="notificationDispatcher">Sends the actual notifications.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public PluginUpdateMonitorHostedService(
        IInstallationManager installationManager,
        IApplicationHost applicationHost,
        NotifiedPluginUpdateRepository notifiedPluginUpdateRepository,
        NotificationDispatcher notificationDispatcher,
        ILogger<PluginUpdateMonitorHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(installationManager);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(notifiedPluginUpdateRepository);
        ArgumentNullException.ThrowIfNull(notificationDispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        _installationManager = installationManager;
        _applicationHost = applicationHost;
        _notifiedPluginUpdateRepository = notifiedPluginUpdateRepository;
        _notificationDispatcher = notificationDispatcher;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lastKnownPendingRestart = _applicationHost.HasPendingRestart;
        _applicationHost.HasPendingRestartChanged += OnHasPendingRestartChanged;
        _timer = new Timer(OnTimerElapsed, null, TimeSpan.Zero, CheckInterval);
        LogMonitorStarted(_logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _applicationHost.HasPendingRestartChanged -= OnHasPendingRestartChanged;
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _timer = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void OnTimerElapsed(object? state)
    {
        _ = CheckForPluginUpdatesSafelyAsync();
    }

    private async Task CheckForPluginUpdatesSafelyAsync()
    {
        try
        {
            await CheckForPluginUpdatesAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogCheckFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Checks for available plugin updates and notifies about any not already notified. Public
    /// visibility (not private) so tests can invoke the actual check logic directly instead of
    /// waiting on the real <see cref="Timer"/> interval.
    /// </summary>
    /// <param name="cancellationToken">Used to cancel the check.</param>
    public async Task CheckForPluginUpdatesAsync(CancellationToken cancellationToken)
    {
        var updates = await _installationManager.GetAvailablePluginUpdates(cancellationToken).ConfigureAwait(false);

        var newUpdates = new List<InstallationInfo>();
        foreach (var update in updates)
        {
            var versionString = update.Version?.ToString() ?? string.Empty;
            if (!_notifiedPluginUpdateRepository.HasBeenNotified(update.Id, versionString))
            {
                newUpdates.Add(update);
            }
        }

        if (newUpdates.Count == 0)
        {
            return;
        }

        foreach (var update in newUpdates)
        {
            _notifiedPluginUpdateRepository.MarkNotified(update.Id, update.Version?.ToString() ?? string.Empty);
        }

        var message = new NotificationMessage
        {
            Title = $"Sentinel: {newUpdates.Count} plugin update(s) available",
            Body = string.Join("\n", newUpdates.Select(u => $"{u.Name} v{u.Version}")),
            Severity = "info",
            IncidentUrl = string.Empty
        };

        await _notificationDispatcher.DispatchMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private void OnHasPendingRestartChanged(object? sender, EventArgs e)
    {
        var isPending = _applicationHost.HasPendingRestart;

        // Only the false-to-true transition fires a notification — this guards against firing
        // again if Jellyfin raises the event more than once while already true, and never fires
        // on the true-to-false transition (a restart that already happened isn't "pending").
        if (isPending && !_lastKnownPendingRestart)
        {
            _lastKnownPendingRestart = true;
            RestartPendingDispatchCount++;
            _ = DispatchRestartPendingNotificationSafelyAsync();
        }
        else if (!isPending)
        {
            _lastKnownPendingRestart = false;
        }
    }

    private async Task DispatchRestartPendingNotificationSafelyAsync()
    {
        try
        {
            await DispatchRestartPendingNotificationAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            LogCheckFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Builds and sends the restart-pending notification. Public (not private) so tests can invoke
    /// it directly instead of needing to raise a real event through a live <see cref="IApplicationHost"/>.
    /// </summary>
    /// <param name="cancellationToken">Used to cancel the dispatch.</param>
    public async Task DispatchRestartPendingNotificationAsync(CancellationToken cancellationToken)
    {
        var hasRecentUpdate = _notifiedPluginUpdateRepository.HasRecentNotification(RecentUpdateWindow);

        var message = new NotificationMessage
        {
            Title = "Sentinel: Server restart is pending",
            Body = BuildRestartPendingBody(hasRecentUpdate),
            Severity = "warning",
            IncidentUrl = string.Empty
        };

        await _notificationDispatcher.DispatchMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the restart-pending notification body, optionally mentioning a recently notified
    /// plugin update as a possible cause. Internal-visible so tests can exercise this pure logic
    /// directly, mirroring the <c>BuildMimeMessage</c>/<c>BuildPayload</c> precedent already
    /// established in <see cref="EmailNotificationChannel"/>/<see cref="DiscordNotificationChannel"/>.
    /// </summary>
    /// <param name="hasRecentUpdate">Whether a plugin-update notification fired within <see cref="RecentUpdateWindow"/>.</param>
    /// <returns>The notification body text.</returns>
    internal static string BuildRestartPendingBody(bool hasRecentUpdate)
    {
        var body = "A server restart is pending.";
        if (hasRecentUpdate)
        {
            body += " This may be related to a recently installed plugin update.";
        }

        return body;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sentinel plugin-update monitor started")]
    private static partial void LogMonitorStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sentinel: plugin-update/restart-pending check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
