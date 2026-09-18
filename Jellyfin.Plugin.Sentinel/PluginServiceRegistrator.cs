using System;
using System.IO;
using Jellyfin.Plugin.Sentinel.Collector;
using Jellyfin.Plugin.Sentinel.Diagnostics;
using Jellyfin.Plugin.Sentinel.Persistence;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Sentinel;

/// <summary>
/// Registers Sentinel's services with Jellyfin's DI container at server startup.
/// Requires a parameterless constructor — cannot be the same class as <see cref="Plugin"/>.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        serviceCollection.AddSingleton(_ =>
        {
            var databasePath = Path.Combine(Plugin.Instance!.DataFolderPath, "sentinel.db");
            return new SentinelDatabase(databasePath);
        });
        serviceCollection.AddSingleton<PlaybackEventRepository>();
        serviceCollection.AddSingleton<DiagnosisRepository>();
        serviceCollection.AddSingleton(new RuleEngine(CoreTranscodeRules.All));
        serviceCollection.AddHostedService<PlaybackCollectorHostedService>();
    }
}
