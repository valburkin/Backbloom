using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Settings.Storage;

namespace Overglow.Settings.Tests;

/// <summary>The first-run flag, its migration for databases already in use, and what a HyperHDR import leaves for the welcome.</summary>
public sealed class SetupStateTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsServices Open(string? legacy = null) =>
        SettingsServices.Open(new SettingsOptions(Path.Combine(_dir.Path, "data"), legacy), TestSchema.Create(), NullLoggerFactory.Instance);

    [Fact]
    public void NewDatabase_WaitsForTheFirstRun_ThenRemembersIt()
    {
        using (var services = Open())
        {
            Assert.False(services.Setup.FirstRunCompleted);
            Assert.Null(services.Setup.ImportedFrom);
            Assert.False(services.Setup.IsAcknowledged(SetupTopics.Discovery));
            services.Setup.CompleteFirstRun();
            services.Setup.Acknowledge(SetupTopics.Discovery);
        }

        using var reopened = Open();
        Assert.True(reopened.Setup.FirstRunCompleted);
        Assert.True(reopened.Setup.IsAcknowledged(SetupTopics.Discovery));
    }

    [Fact]
    public void DatabaseFromAnEarlierVersion_CountsAsStarted()
    {
        // an Overglow 0.1 database (schema 2) that has been in use: updating must not hold its lights
        var path = Path.Combine(_dir.Path, "data", SettingsDatabase.FileName);
        using (var v2 = SettingsDatabase.Open(path, [.. SettingsDatabase.Migrations.Take(2)]))
        {
            v2.SetMeta("initialized_at", "2026-10-01T00:00:00.000Z");
        }

        using var services = Open();
        Assert.Equal(SettingsDatabase.CurrentVersion, services.Database.Version);
        Assert.True(services.Setup.FirstRunCompleted);
        Assert.Equal("upgrade", services.Database.GetMeta(SqliteSetupState.FirstRunKey));
    }

    [Fact]
    public void HyperHdrImport_WaitsForTheWelcome_WithRemoteAccessOff()
    {
        var legacy = Path.Combine(_dir.Path, "HyperHDR");
        Directory.CreateDirectory(Path.Combine(legacy, "db"));
        File.Copy(Fixtures.OwnerDatabase, Path.Combine(legacy, DataDirectories.LegacyDatabase));

        using var services = Open(legacy);

        Assert.False(services.Setup.FirstRunCompleted);
        Assert.EndsWith("hyperhdr.db", services.Setup.ImportedFrom, StringComparison.OrdinalIgnoreCase);
        var network = services.Store.GetSection("network")!;
        Assert.False(network["remoteAccess"]!.GetValue<bool>());
        Assert.Equal(168, services.Store.GetSection("leds")!.AsArray().Count);
    }

    [Fact]
    public void RemoteAccess_IsOffByDefault()
    {
        using var services = Open();
        Assert.False(services.Store.GetSection("network")!["remoteAccess"]!.GetValue<bool>());
    }
}
