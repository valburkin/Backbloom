using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Settings.Storage;

namespace Backbloom.Settings.Tests;

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
