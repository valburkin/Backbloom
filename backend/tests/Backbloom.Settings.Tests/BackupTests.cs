using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Settings.Import;

namespace Backbloom.Settings.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsServices Open(string name) =>
        SettingsServices.Open(new SettingsOptions(Path.Combine(_dir.Path, name), null), TestSchema.Create(), NullLoggerFactory.Instance);

    [Fact]
    public void Backup_RoundTripsEverythingIntoAnotherInstallation()
    {
        using var source = Open("a");
        source.ImportDatabase(Fixtures.OwnerDatabase);
        Assert.True(source.Store.Update(TestSchema.Parse("""
            { "layout": { "version": 1,
                          "room": { "width": 4, "depth": 4, "height": 2.6, "screen": { "x": 1.7, "y": 1.31, "width": 0.6, "height": 0.34 },
                                    "furniture": "desk" },
                          "controllers": [{ "id": "c", "name": "First LED instance", "outputs": [{ "index": 0, "start": 0, "length": 168 }] }],
                          "strips": [{ "id": "s", "name": "Top", "controllerId": "c", "output": 0, "ledCount": 168,
                                       "path": [{ "surface": "front", "x": 1.7, "y": 1.31 }, { "surface": "front", "x": 2.3, "y": 1.31 }] }] } }
            """)).Success);
        var backup = SettingsBackup.Create(source.Store.Export());

        Assert.Equal(SettingsBackup.Version, backup["version"]!.GetValue<string>());
        Assert.Equal("""[{"instance":0,"friendly_name":"First LED instance","enabled":1}]""", backup["instances"]!.ToJsonString());
        Assert.All(backup["settings"]!.AsArray(), s => Assert.Equal(System.Text.Json.JsonValueKind.String, s!["config"]!.GetValueKind()));

        using var target = Open("b");
        var changed = new List<string>();
        target.Store.Changed += c => changed.AddRange(c.Sections.Keys);
        var outcome = target.RestoreBackup(JsonNode.Parse(backup.ToJsonString())!.AsObject());

        Assert.True(outcome.Stored.Success);
        Assert.Empty(outcome.Result.Changes);
        var expected = source.Store.Snapshot();
        var actual = target.Store.Snapshot();
        Assert.Equal(expected.Select(kv => kv.Key), actual.Select(kv => kv.Key));
        Assert.All(expected, kv => Assert.Equal(kv.Value!.ToJsonString(), actual[kv.Key]!.ToJsonString()));
        Assert.Contains("leds", changed);
        Assert.Contains("device", changed);
        Assert.Null(outcome.BackupPath);
        Assert.Null(target.Auth.Load().Password);

        var again = target.RestoreBackup(backup);
        Assert.Empty(again.Stored.ChangedSections);
        Assert.True(File.Exists(again.BackupPath));
    }

    [Fact]
    public void Restore_AcceptsHyperHdrBackupsAndMigratesThem()
    {
        var backup = TestSchema.Parse("""
            { "version": "HyperHDR_export_format_v20",
              "instances": [{ "instance": 0, "friendly_name": "TV", "enabled": 1 }],
              "settings": [
                { "type": "general", "config": "{\"name\":\"My HyperHDR Config\",\"version\":6,\"disableOnLocked\":true}" },
                { "type": "foregroundEffect", "config": "{\"effect\":\"Knight rider\",\"enable\":true}", "hyperhdr_instance": 0 },
                { "type": "device", "config": "{\"type\":\"ws2812spi\",\"colorOrder\":\"grb\"}", "hyperhdr_instance": 0 },
                { "type": "videoGrabber", "config": "{}" } ] }
            """);

        using var services = Open("a");
        var outcome = services.RestoreBackup(backup);

        Assert.True(outcome.Stored.Success);
        Assert.Equal("Breath", services.Store.GetSection("foregroundEffect")!["effect"]!.GetValue<string>());
        Assert.Equal(("file", "grb"), (services.Store.GetSection("device")!["type"]!.GetValue<string>(), services.Store.GetSection("device")!["colorOrder"]!.GetValue<string>()));
        Assert.Equal("My Backbloom Config", services.Store.GetSection("general")!["name"]!.GetValue<string>());
        Assert.Equal("TV", services.Store.Controllers[0].Name);
        Assert.Contains("Removed the 'videoGrabber' settings: the feature is not part of this version", outcome.Result.Changes);
    }

    [Theory]
    [InlineData("""{ "settings": [] }""")]
    [InlineData("""{ "version": "Something_else", "settings": [] }""")]
    [InlineData("""{ "version": "Backbloom_export_format_v1" }""")]
    public void Restore_RejectsFilesThatArentBackups(string json)
    {
        using var services = Open("a");

        Assert.Throws<InvalidDataException>(() => services.RestoreBackup(TestSchema.Parse(json)));
    }
}
