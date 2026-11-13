using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Settings.Auth;
using Backbloom.Settings.Import;

namespace Backbloom.Settings.Tests;

public sealed class ImporterTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private SettingsServices OpenServices(string? legacyDirectory = null) =>
        SettingsServices.Open(new SettingsOptions(Path.Combine(_dir.Path, "data"), legacyDirectory), TestSchema.Create(), NullLoggerFactory.Instance);

    private static bool Verify(SecretHash hash, string secret) => hash.Algorithm == SecretAlgorithm.LegacySha512 &&
        CryptographicOperations.FixedTimeEquals(SHA512.HashData([.. Encoding.UTF8.GetBytes(secret), .. hash.Salt]), hash.Hash);

    [Fact]
    public void ReferenceDatabase_KeepsLedsColorSmoothingDeviceEffectsAndCapture()
    {
        using var services = OpenServices();
        var outcome = services.ImportDatabase(Fixtures.ReferenceDatabase);

        Assert.True(outcome.Stored.Success, string.Join("; ", outcome.Stored.Errors));
        var store = services.Store;
        var leds = store.GetSection("leds")!.AsArray();
        Assert.Equal(168, leds.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"hmin":0.9694,"hmax":1,"vmin":0.8,"vmax":1,"group":0}"""), leds[0]));

        var color = store.GetSection("color")!;
        Assert.Equal("advanced", color["imageToLedMappingType"]!.GetValue<string>());
        Assert.Equal(1.5, color["channelAdjustment"]![0]!["gamma"]!.GetValue<double>());
        Assert.Equal(0.0039, color["channelAdjustment"]![0]!["backlightThreshold"]!.GetValue<double>());

        var smoothing = store.GetSection("smoothing")!;
        Assert.Equal(("HybridRgbInterpolator", 150, 50.0, false), (
            smoothing["type"]!.GetValue<string>(), smoothing["time_ms"]!.GetValue<int>(),
            smoothing["updateFrequency"]!.GetValue<double>(), smoothing["enable"]!.GetValue<bool>()));
        Assert.Equal(0.03, smoothing["y_limit"]!.GetValue<double>());

        var device = store.GetSection("device")!;
        Assert.Equal(("wled", 40, 20, "rgb"), (
            device["type"]!.GetValue<string>(), device["brightnessMaxLevel"]!.GetValue<int>(),
            device["refreshTime"]!.GetValue<int>(), device["colorOrder"]!.GetValue<string>()));
        var controller = Assert.Single(store.Controllers);
        Assert.Equal(("First LED instance", true), (controller.Name, controller.Enabled));

        Assert.Equal("Rainbow swirl fast", store.GetSection("foregroundEffect")!["effect"]!.GetValue<string>());
        Assert.Equal(3000, store.GetSection("foregroundEffect")!["duration_ms"]!.GetValue<int>());
        Assert.Equal("Rainbow swirl fast", store.GetSection("backgroundEffect")!["effect"]!.GetValue<string>());

        var capture = store.GetSection("systemGrabber")!.AsObject();
        // the reference setup had tone mapping on for an SDR monitor, which oversaturated the LEDs: it becomes "auto"
        Assert.Equal((30, 512, "auto", true, 200), (
            capture["fps"]!.GetValue<int>(), capture["videoMode"]!.GetValue<int>(), capture["hdrToneMapping"]!.GetValue<string>(),
            capture["hardware"]!.GetValue<bool>(), capture["monitor_nits"]!.GetValue<int>()));
        Assert.False(capture.ContainsKey("signalDetection"));
        Assert.False(store.GetSection("systemControl")!.AsObject().ContainsKey("cecControl"));
        Assert.Equal("My Backbloom Config", store.GetSection("general")!["name"]!.GetValue<string>());
        Assert.Equal(100, store.GetSection("ledConfig")!["classic"]!["top"]!.GetValue<int>());

        // HyperHDR's localAdminAuth (on in the reference setup) and the other access options are gone
        Assert.Equal("""{"remoteAccess":false}""", store.GetSection("network")!.ToJsonString());
        Assert.Contains(outcome.Result.Changes, c => c.StartsWith("Removed the network access options (", StringComparison.Ordinal) && c.Contains("localAdminAuth", StringComparison.Ordinal));
        Assert.Null(store.GetSection("layout"));

        foreach (var removed in new[] { "videoGrabber", "videoControl", "videoDetection", "automaticToneMapping", "flatbufServer", "protoServer", "jsonServer", "forwarder", "rawUdpServer" })
        {
            Assert.Contains($"Removed the '{removed}' settings: the feature is not part of this version", outcome.Result.Changes);
        }

        var auth = services.Auth.Load();
        Assert.True(Verify(auth.Password!, Fixtures.FixturePassword));
        Assert.False(Verify(auth.Password!, "hyperhdr"));
        Assert.Equal("00000000-0000-5000-8000-000000000001", services.Database.GetOrCreateInstanceId());
        Assert.DoesNotContain(outcome.Result.Changes, c => c.Contains(Fixtures.FixturePassword, StringComparison.Ordinal));
    }

    [Fact]
    public void Import_NeverModifiesTheSourceAndIsIdempotent()
    {
        var source = _dir.File("hyperhdr.db");
        File.Copy(Fixtures.ReferenceDatabase, source);
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var stamp = File.GetLastWriteTimeUtc(source);

        using var services = OpenServices();
        services.ImportDatabase(source);
        var first = SettingsBackup.Create(services.Store.Export());
        var changes = 0;
        services.Store.Changed += _ => changes++;
        var second = services.ImportDatabase(source);

        Assert.True(second.Stored.Success);
        Assert.Empty(second.Stored.ChangedSections);
        Assert.Equal(0, changes);
        Assert.True(JsonNode.DeepEquals(first, SettingsBackup.Create(services.Store.Export())));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "data", "imported", "hyperhdr.db")));
        Assert.NotNull(second.BackupPath);
    }

    [Fact]
    public void FirstStart_ImportsFromTheLegacyDirectoryOnce()
    {
        var legacy = Path.Combine(_dir.Path, "HyperHDR");
        Directory.CreateDirectory(Path.Combine(legacy, "db"));
        File.Copy(Fixtures.ReferenceDatabase, Path.Combine(legacy, "db", "hyperhdr.db"));

        using (var services = OpenServices(legacy))
        {
            Assert.Equal(168, services.Store.GetSection("leds")!.AsArray().Count);
            Assert.True(services.Store.Update(TestSchema.Parse("""{ "logger": { "level": "silent" } }""")).Success);
        }

        using var reopened = OpenServices(legacy);
        Assert.Equal("silent", reopened.Store.GetSection("logger")!["level"]!.GetValue<string>());
    }

    [Fact]
    public void FirstStart_WithoutLegacyDataStartsWithDefaults()
    {
        using var services = OpenServices(Path.Combine(_dir.Path, "missing"));

        Assert.Equal("My Backbloom Config", services.Store.GetSection("general")!["name"]!.GetValue<string>());
        Assert.Same(AuthData.Empty, services.Auth.Load());
    }

    [Fact]
    public void FirstStart_ABrokenSourceIsLoggedAndDefaultsAreUsed()
    {
        var legacy = Path.Combine(_dir.Path, "HyperHDR");
        Directory.CreateDirectory(Path.Combine(legacy, "db"));
        File.WriteAllText(Path.Combine(legacy, "db", "hyperhdr.db"), "not a database");

        using var services = OpenServices(legacy);

        Assert.Single(services.Store.GetSection("leds")!.AsArray());
    }

    [Fact]
    public void Instances_BecomeControllersFedByThePipelineOfTheFirstInstance()
    {
        var path = new LegacyDatabaseBuilder()
            .Instance(0, "TV")
            .Instance(1, "Desk", enabled: false)
            .Instance(2, "Shelf")
            .Setting("general", null, """{ "name": "Home", "version": 6, "disableOnLocked": true }""")
            .Setting("leds", 0, """[{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 0.1 }, { "hmin": 0, "hmax": 1, "vmin": 0.9, "vmax": 1 }]""")
            .Setting("leds", 1, """[{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1 }]""")
            .Setting("smoothing", 1, """{ "time_ms": 400 }""")
            .Setting("device", 0, """{ "type": "wled", "host": "tv.local", "colorOrder": "grb" }""")
            .Setting("device", 1, """{ "type": "philipshue", "colorOrder": "rgb" }""")
            .Setting("device", 2, """{ "type": "file", "output": "shelf.txt" }""")
            .Write(_dir.File("multi.db"));

        using var services = OpenServices();
        var outcome = services.ImportDatabase(path);

        var controllers = services.Store.Controllers;
        Assert.Equal([(0, "TV", true), (1, "Desk", false), (2, "Shelf", true)], controllers.Select(c => (c.Id, c.Name, c.Enabled)));
        Assert.Equal("tv.local", services.Store.GetSection("device")!["host"]!.GetValue<string>());
        Assert.Equal("file", controllers[1].Device["type"]!.GetValue<string>());
        Assert.Equal("shelf.txt", controllers[2].Device["output"]!.GetValue<string>());
        Assert.Equal(2, services.Store.GetSection("leds")!.AsArray().Count);
        Assert.Equal(350, services.Store.GetSection("smoothing")!["time_ms"]!.GetValue<int>());
        Assert.Contains(outcome.Result.Changes, c => c.StartsWith("Instance 'Desk' became LED controller 1; its own leds, smoothing settings were not imported", StringComparison.Ordinal));
        Assert.Contains(outcome.Result.Changes, c => c.StartsWith("Instance 'Desk': The LED device type 'philipshue'", StringComparison.Ordinal));
    }

    [Fact]
    public void Credentials_HyperHdrPasswordAndTokensKeepWorking_FactoryPasswordIsReset()
    {
        var path = new LegacyDatabaseBuilder()
            .Instance(0, "TV")
            .User("Hyperhdr", "my secret password", "0123abcd")
            .Token("a1b2c", "Stream Deck", "6f1ed002-ab5d-42e8-a8b9-6e8b4f0d1c2a")
            .Write(_dir.File("auth.db"));

        using var services = OpenServices();
        var outcome = services.ImportDatabase(path);

        var auth = services.Auth.Load();
        Assert.True(Verify(auth.Password!, "my secret password"));
        var token = Assert.Single(auth.Tokens);
        Assert.Equal(("a1b2c", "Stream Deck"), (token.Id, token.Comment));
        Assert.True(Verify(token.Secret, "6f1ed002-ab5d-42e8-a8b9-6e8b4f0d1c2a"));
        Assert.Contains("Imported the admin password (it keeps working)", outcome.Result.Changes);
        Assert.DoesNotContain(outcome.Result.Changes, c => c.Contains("my secret", StringComparison.Ordinal));

        var factory = new LegacyDatabaseBuilder().User("Hyperhdr", "hyperhdr", "ffee").Write(_dir.File("factory.db"));
        services.ImportDatabase(factory);
        Assert.Null(services.Auth.Load().Password);
    }

    [Fact]
    public void Import_RepairsInvalidValuesAndReportsEachFix()
    {
        var path = new LegacyDatabaseBuilder()
            .Instance(0, "TV")
            .Setting("smoothing", 0, """{ "time_ms": 99999, "type": "Stepper" }""")
            .Setting("logger", null, """{ "level": "loud" }""")
            .Setting("color", 0, "not json")
            .Setting("grabberV4L2", null, "{}")
            .Setting("instCapture", 0, "{}")
            .Write(_dir.File("broken.db"));

        using var services = OpenServices();
        var outcome = services.ImportDatabase(path);

        Assert.True(outcome.Stored.Success);
        Assert.Equal(5000, services.Store.GetSection("smoothing")!["time_ms"]!.GetValue<int>());
        Assert.Equal("warn", services.Store.GetSection("logger")!["level"]!.GetValue<string>());
        Assert.Contains("Fixed smoothing.time_ms: was above 5000; set to 5000", outcome.Result.Changes);
        Assert.Contains("The 'color' settings couldn't be read (not valid JSON); defaults are used", outcome.Result.Changes);
        Assert.Contains("Removed the 'grabberV4L2' settings: the feature is not part of this version", outcome.Result.Changes);
        Assert.Contains("Removed the unknown 'instCapture' settings", outcome.Result.Changes);
    }

    [Fact]
    public void Import_RefusesFilesThatArentHyperHdrDatabases()
    {
        File.WriteAllText(_dir.File("notes.db"), "hello");
        using var services = OpenServices();

        Assert.Throws<InvalidDataException>(() => services.ImportDatabase(_dir.File("notes.db")));
        Assert.Throws<InvalidDataException>(() => services.ImportDatabase(services.Database.FilePath));
    }
}
