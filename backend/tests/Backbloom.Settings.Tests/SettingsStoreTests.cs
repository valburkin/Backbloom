using System.Text.Json.Nodes;
using Backbloom.Settings.Auth;
using Backbloom.Settings.Storage;
using static Backbloom.Settings.Tests.TestSchema;

namespace Backbloom.Settings.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly List<OpenStore> _open = [];

    public void Dispose()
    {
        _open.ForEach(o => o.Dispose());
        _dir.Dispose();
    }

    private SqliteSettingsStore Open() => Track(OpenStore.In(_dir.Path)).Store;

    private OpenStore Track(OpenStore store)
    {
        _open.Add(store);
        return store;
    }

    private void CloseAll()
    {
        _open.ForEach(o => o.Dispose());
        _open.Clear();
    }

    [Fact]
    public void NewStore_HasSchemaDefaultsForEverySectionButNoLayout()
    {
        var all = Open().Snapshot();

        Assert.Equal(SettingsSchema.BuiltinSections.Where(s => s != "layout"), all.Select(kv => kv.Key));
        Assert.Equal("file", all["device"]!["type"]!.GetValue<string>());
        Assert.Equal("/dev/null", all["device"]!["output"]!.GetValue<string>());
        Assert.Equal(1.5, all["color"]!["channelAdjustment"]![0]!["gamma"]!.GetValue<double>());
        Assert.Equal("HybridRgbInterpolator", all["smoothing"]!["type"]!.GetValue<string>());
        Assert.Equal("Breath", all["foregroundEffect"]!["effect"]!.GetValue<string>());
        Assert.Equal("Warm mood blobs", all["backgroundEffect"]!["effect"]!.GetValue<string>());
        Assert.Equal(30, all["systemGrabber"]!["fps"]!.GetValue<int>());
        Assert.True(all["systemGrabber"]!["monitor_nits_auto"]!.GetValue<bool>());
        Assert.Equal(["systemInstancePriority"], all["systemControl"]!.AsObject().Select(p => p.Key));
        Assert.Single(all["leds"]!.AsArray());
        Assert.Equal(["remoteAccess"], all["network"]!.AsObject().Select(p => p.Key));
        var controller = Assert.Single(Open().Controllers);
        Assert.Equal((0, true), (controller.Id, controller.Enabled));
    }

    [Fact]
    public void Update_RejectsOutOfRangeValuesWithPerFieldErrorsAndStoresNothing()
    {
        var store = Open();
        var color = store.GetSection("color")!.AsObject();
        color["channelAdjustment"]![0]!["gamma"] = 101;
        color["channelAdjustment"]![0]!["scaleOutput"] = -1;
        var changes = 0;
        store.Changed += _ => changes++;

        var result = store.Update(new JsonObject { ["color"] = color, ["general"] = Parse("""{ "name": "ab" }""") });

        Assert.False(result.Success);
        Assert.Equal(
            new HashSet<SettingsError>
            {
                new("color.channelAdjustment.0.scaleOutput", "must be at least 0"),
                new("color.channelAdjustment.0.gamma", "must be at most 100"),
                new("general.name", "must be at least 4 characters long"),
            },
            result.Errors.ToHashSet());
        Assert.Equal(0, changes);
        CloseAll();
        Assert.Equal(1.5, Open().GetSection("color")!["channelAdjustment"]![0]!["gamma"]!.GetValue<double>());
    }

    [Fact]
    public void Update_FillsMissingRequiredValuesAndPersists()
    {
        var store = Open();
        SettingsChange? change = null;
        store.Changed += c => change = c;

        var result = store.Update(Parse("""
            { "leds": [{ "hmin": 0, "hmax": 0.5, "vmin": 0, "vmax": 1 }, { "hmin": 0.5, "hmax": 1, "vmin": 0, "vmax": 1, "disabled": true }],
              "device": { "type": "wled", "host": "wled-test.local" } }
            """));

        Assert.True(result.Success);
        Assert.Equal(["leds", "device"], result.ChangedSections);
        Assert.NotNull(change);
        Assert.Equal(0, store.GetSection("leds")![0]!["group"]!.GetValue<int>());
        Assert.Equal(255, store.GetSection("device")!["brightnessMaxLevel"]!.GetValue<int>());
        Assert.Equal("rgb", store.GetSection("device")!["colorOrder"]!.GetValue<string>());
        var snapshot = store.Snapshot();

        CloseAll();
        var reopened = Open();
        Assert.True(JsonNode.DeepEquals(snapshot, reopened.Snapshot()));
        Assert.True(reopened.GetSection("leds")![1]!["disabled"]!.GetValue<bool>());
        Assert.Equal("wled", reopened.Controllers[0].Device["type"]!.GetValue<string>());
    }

    [Fact]
    public void Update_ReportsOnlySectionsThatChanged()
    {
        var store = Open();
        var general = store.GetSection("general")!;
        var color = store.GetSection("color")!.AsObject();
        color["imageToLedMappingType"] = "unicolor_mean";

        var result = store.Update(new JsonObject { ["general"] = general, ["color"] = color });

        Assert.Equal(["color"], result.ChangedSections);
    }

    [Fact]
    public void Update_KeepsLayoutPropertiesTheBackendDoesntUse()
    {
        var store = Open();

        var result = store.Update(Parse("""
            { "layout": { "version": 1,
                          "room": { "width": 4, "depth": 4, "height": 2.6, "screen": { "x": 1.7, "y": 1.31, "width": 0.6, "height": 0.34 },
                                    "furniture": "none" },
                          "controllers": [{ "id": "c", "host": "x.local", "outputs": [{ "index": 0, "start": 0, "length": 1, "pin": 2 }] }],
                          "strips": [], "extra": true } }
            """));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var layout = store.GetSection("layout")!;
        Assert.True(layout["extra"]!.GetValue<bool>());
        Assert.Equal("x.local", layout["controllers"]![0]!["host"]!.GetValue<string>());
        Assert.Equal(2, layout["controllers"]![0]!["outputs"]![0]!["pin"]!.GetValue<int>());
    }

    [Fact]
    public void Update_IsOneTransaction_AFailingWriteChangesNothing()
    {
        var open = Track(OpenStore.In(_dir.Path));
        open.Database.Write((c, t) => SettingsDatabase.Execute(c, t, """
            CREATE TRIGGER fail_logger BEFORE INSERT ON settings WHEN NEW.section = 'logger'
            BEGIN SELECT RAISE(ABORT, 'disk full'); END
            """));
        var changes = 0;
        open.Store.Changed += _ => changes++;

        var result = open.Store.Update(Parse("""{ "general": { "name": "Living room" }, "logger": { "level": "silent" } }"""));

        Assert.False(result.Success);
        Assert.Contains("disk full", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
        Assert.Equal(0, changes);
        Assert.Equal("My Backbloom Config", open.Store.GetSection("general")!["name"]!.GetValue<string>());
        CloseAll();
        Assert.Equal("My Backbloom Config", Open().GetSection("general")!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_ConcurrentWritersAllPersistAndReadersSeeValidSnapshots()
    {
        var store = Open();
        var levels = new[] { "silent", "warn", "verbose", "debug" };
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                var section = (w % 2 == 0) ? Parse($$"""{ "general": { "name": "Writer {{w}} #{{i}}" } }""") : Parse($$"""{ "logger": { "level": "{{levels[i % 4]}}" } }""");
                Assert.True(store.Update(section).Success);
                Assert.True(store.Snapshot().ContainsKey("color"));
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(writers);

        var name = store.GetSection("general")!["name"]!.GetValue<string>();
        Assert.EndsWith("#24", name, StringComparison.Ordinal);
        var snapshot = store.Snapshot();
        CloseAll();
        Assert.True(JsonNode.DeepEquals(snapshot, Open().Snapshot()));
    }

    [Fact]
    public void Load_RepairsSectionsThatNoLongerValidateAndKeepsUnknownRows()
    {
        var open = Track(OpenStore.In(_dir.Path));
        open.Database.Write((c, t) =>
        {
            SettingsDatabase.Execute(c, t, "INSERT INTO settings VALUES ('smoothing', '{\"time_ms\": 99999, \"type\": \"Stepper\"}', 'now')");
            SettingsDatabase.Execute(c, t, "INSERT INTO settings VALUES ('futureFeature', '{\"on\": true}', 'now')");
        });
        CloseAll();

        var store = Open();
        Assert.Equal(5000, store.GetSection("smoothing")!["time_ms"]!.GetValue<int>());
        Assert.Equal("Stepper", store.GetSection("smoothing")!["type"]!.GetValue<string>());
        Assert.Null(store.GetSection("futureFeature"));

        store.Update(Parse("""{ "logger": { "level": "silent" } }"""));
        var future = _open[^1].Database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE section = 'futureFeature'";
            return command.ExecuteScalar() as string;
        });
        Assert.Equal("{\"on\": true}", future);
    }

    [Fact]
    public void Replace_ValidatesEverythingAndFillsDefaults()
    {
        var store = Open();
        var content = store.Export();
        content.Sections.Remove("smoothing");
        content.Sections["logger"] = Parse("""{ "level": "silent" }""");
        var changed = new List<string>();
        store.Changed += c => changed.AddRange(c.Sections.Keys);

        var invalid = store.Replace(content with { Controllers = [content.Controllers[0] with { Id = 3 }] });
        Assert.Contains(invalid.Errors, e => e.Path == "controllers");

        var controllers = new[] { content.Controllers[0], new ControllerSettings(1, "Desk", false, Parse("""{ "type": "wled", "host": "desk.local" }""")) };
        var result = store.Replace(content with { Controllers = controllers });

        Assert.True(result.Success);
        Assert.Equal(["logger"], changed);
        Assert.Equal(350, store.GetSection("smoothing")!["time_ms"]!.GetValue<int>());
        Assert.Equal(["LED controller", "Desk"], store.Controllers.Select(c => c.Name));
        Assert.Equal(255, store.Controllers[1].Device["brightnessMaxLevel"]!.GetValue<int>());
    }

    [Fact]
    public void Database_AppliesForwardMigrationsInOrderAndRefusesNewerFiles()
    {
        var path = _dir.File("migrations.db");
        var applied = new List<int>();
        SchemaMigration Step(int version) => new(version, $"step {version}", (c, t) =>
        {
            applied.Add(version);
            SettingsDatabase.Execute(c, t, $"CREATE TABLE t{version} (x)");
        });

        SettingsDatabase.Open(path, [Step(1)]).Dispose();
        using (var upgraded = SettingsDatabase.Open(path, [Step(1), Step(2), Step(3)]))
        {
            Assert.Equal(3, upgraded.Version);
            Assert.False(upgraded.Created);
        }

        Assert.Equal([1, 2, 3], applied);
        var error = Assert.Throws<InvalidDataException>(() => SettingsDatabase.Open(path, [Step(1), Step(2)]));
        Assert.Contains("newer Backbloom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Database_RefusesFilesThatArentBackbloomSettings()
    {
        var path = new LegacyDatabaseBuilder().Write(_dir.File("hyperhdr.db"));

        Assert.Throws<InvalidDataException>(() => SettingsDatabase.Open(path));
    }

    [Fact]
    public void ClientSchema_ContainsEverySectionAndTheDriverSchemas()
    {
        var properties = Create().ToClientSchema()["properties"]!.AsObject();

        Assert.Equal([.. SettingsSchema.BuiltinSections, "alldevices"], properties.Select(p => p.Key));
        Assert.Equal(["file", "wled"], properties["alldevices"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void AuthStore_RoundTripsHashesAndTokens()
    {
        var open = Track(OpenStore.In(_dir.Path));
        var store = new SqliteAuthStore(open.Database);
        Assert.Same(AuthData.Empty, store.Load());

        var data = new AuthData(
            new SecretHash([1, 2, 3], [4, 5], 1000),
            [
                new ApiToken("a1b2c", "Stream Deck", new SecretHash([9], [8], 10), new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
                new ApiToken("ffff0", "Old", new SecretHash([7], [], 0, SecretAlgorithm.LegacySha512), null),
            ]);
        store.Save(data);
        CloseAll();
        var loaded = new SqliteAuthStore(Track(OpenStore.In(_dir.Path)).Database).Load();

        Assert.Equal([1, 2, 3], loaded.Password!.Hash);
        Assert.Equal(1000, loaded.Password.Iterations);
        Assert.Equal(["a1b2c", "ffff0"], loaded.Tokens.Select(t => t.Id));
        Assert.Equal(("Stream Deck", data.Tokens[0].LastUse), (loaded.Tokens[0].Comment, loaded.Tokens[0].LastUse));
        Assert.Equal(SecretAlgorithm.LegacySha512, loaded.Tokens[1].Secret.Algorithm);
    }

    [Fact]
    public void InstanceId_IsCreatedOnceAndKept()
    {
        var open = Track(OpenStore.In(_dir.Path));
        var id = open.Database.GetOrCreateInstanceId();

        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(id, open.Database.GetOrCreateInstanceId());
    }
}
