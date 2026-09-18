using System;
using System.IO;
using Jellyfin.Plugin.Sentinel.Collector;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sentinel;

/// <summary>
/// Registers Sentinel's services with Jellyfin's DI container at server startup.
/// Requires a parameterless constructor — cannot be the same class as <see cref="Plugin"/>.
/// </summary>
public sealed partial class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        // This factory runs the first time the DI container resolves SentinelDatabase — which
        // happens while Jellyfin itself is starting up (resolving the IHostedService below). An
        // unhandled exception here would fail Host.StartAsync() and take down the *entire*
        // Jellyfin server, not just this plugin. Every failure path is therefore logged with
        // enough context to diagnose before it is rethrown.
        serviceCollection.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<Plugin>>();

            if (Plugin.Instance is null)
            {
                LogPluginInstanceNull(logger);
                throw new InvalidOperationException(
                    "Sentinel: Plugin.Instance was null when resolving the database path — this indicates a plugin loading order problem.");
            }

            var dataFolderPath = Plugin.Instance.DataFolderPath;
            Directory.CreateDirectory(dataFolderPath);
            var databasePath = Path.Combine(dataFolderPath, "sentinel.db");

            try
            {
                var database = new SentinelDatabase(databasePath);
                LogDatabaseInitialized(logger, databasePath);
                return database;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                LogDatabaseInitializationFailed(logger, databasePath, ex.Message, ex);
                throw;
            }
#pragma warning restore CA1031
        });
        serviceCollection.AddSingleton<PlaybackEventRepository>();
        serviceCollection.AddSingleton<DiagnosisRepository>();
        serviceCollection.AddSingleton(new RuleEngine(AllDiagnosticRules.All));
        serviceCollection.AddSingleton(TimeProvider.System);
        serviceCollection.AddHostedService<PlaybackCollectorHostedService>();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sentinel: Plugin.Instance was null when resolving the database path — this indicates a plugin loading order problem.")]
    private static partial void LogPluginInstanceNull(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sentinel: failed to initialize its database at {Path} — Sentinel will not function until this is resolved: {Message}")]
    private static partial void LogDatabaseInitializationFailed(ILogger logger, string path, string message, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sentinel: database initialized at {Path}")]
    private static partial void LogDatabaseInitialized(ILogger logger, string path);
}
