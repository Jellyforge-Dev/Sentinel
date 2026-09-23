using System;
using System.IO;
using Jellyfin.Plugin.Sentinel.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Persistence;

public class NotifiedPluginUpdateRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SentinelDatabase _database;
    private readonly NotifiedPluginUpdateRepository _repository;

    public NotifiedPluginUpdateRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"sentinel-notified-plugin-update-test-{Guid.NewGuid()}.db");
        _database = new SentinelDatabase(_databasePath);
        _repository = new NotifiedPluginUpdateRepository(_database);
    }

    [Fact]
    public void HasBeenNotified_ReturnsFalse_ForUnseenPluginVersion()
    {
        var pluginId = Guid.NewGuid();

        Assert.False(_repository.HasBeenNotified(pluginId, "1.0.0"));
    }

    [Fact]
    public void HasBeenNotified_ReturnsTrue_AfterMarkNotified()
    {
        var pluginId = Guid.NewGuid();

        _repository.MarkNotified(pluginId, "1.0.0");

        Assert.True(_repository.HasBeenNotified(pluginId, "1.0.0"));
    }

    [Fact]
    public void MarkNotified_IsIdempotent_WhenCalledTwiceForTheSamePluginVersion()
    {
        var pluginId = Guid.NewGuid();

        var exception = Record.Exception(() =>
        {
            _repository.MarkNotified(pluginId, "1.0.0");
            _repository.MarkNotified(pluginId, "1.0.0");
        });

        Assert.Null(exception);
        Assert.Equal(1, CountRows(pluginId, "1.0.0"));
    }

    [Fact]
    public void HasRecentNotification_ReturnsTrue_WithinWindow_AndFalse_OutsideIt()
    {
        var pluginId = Guid.NewGuid();
        _repository.MarkNotified(pluginId, "1.0.0");

        Assert.True(_repository.HasRecentNotification(TimeSpan.FromMinutes(10)));

        SetNotifiedAtUtc(pluginId, "1.0.0", DateTime.UtcNow.AddMinutes(-20));

        Assert.False(_repository.HasRecentNotification(TimeSpan.FromMinutes(10)));
    }

    private long CountRows(Guid pluginId, string version)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM NotifiedPluginUpdate WHERE PluginId = $pluginId AND Version = $version;";
        command.Parameters.AddWithValue("$pluginId", pluginId.ToString());
        command.Parameters.AddWithValue("$version", version);
        return (long)command.ExecuteScalar()!;
    }

    private void SetNotifiedAtUtc(Guid pluginId, string version, DateTime notifiedAtUtc)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NotifiedPluginUpdate SET NotifiedAtUtc = $notifiedAtUtc WHERE PluginId = $pluginId AND Version = $version;";
        command.Parameters.AddWithValue("$notifiedAtUtc", notifiedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$pluginId", pluginId.ToString());
        command.Parameters.AddWithValue("$version", version);
        command.ExecuteNonQuery();
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
