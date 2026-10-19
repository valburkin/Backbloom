using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Settings.Auth;

namespace Overglow.Settings.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"overglow-settings-{Guid.NewGuid():N}");

    private static SettingsSchema Schema() => SettingsSchema.CreateBuiltin(new Dictionary<string, JsonObject>
    {
        ["file"] = JsonNode.Parse("""
            { "type": "object", "properties": {
                "output": { "type": "string", "default": "/dev/null" },
                "printTimeStamp": { "type": "boolean", "default": true, "required": true } },
              "additionalProperties": true }
            """)!.AsObject(),
        ["wled"] = JsonNode.Parse("""
            { "type": "object", "properties": {
                "host": { "type": "string" },
                "brightnessMaxLevel": { "type": "integer", "minimum": 1, "maximum": 255, "default": 255, "required": true } },
              "additionalProperties": true }
            """)!.AsObject(),
    });

    private JsonFileSettingsStore Open() => new(_dir, Schema(), NullLogger<JsonFileSettingsStore>.Instance);

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void NewStore_HasSchemaDefaultsButNoLayout()
    {
        var all = Open().Snapshot();

        Assert.Equal("file", all["device"]!["type"]!.GetValue<string>());
        Assert.Equal("rgb", all["device"]!["colorOrder"]!.GetValue<string>());
        Assert.Equal("/dev/null", all["device"]!["output"]!.GetValue<string>());
        Assert.Equal(1.5, all["color"]!["channelAdjustment"]![0]!["gamma"]!.GetValue<double>());
        Assert.Equal("advanced", all["color"]!["imageToLedMappingType"]!.GetValue<string>());
        Assert.Single(all["leds"]!.AsArray());
        Assert.True(all["network"]!["localAdminAuth"]!.GetValue<bool>());
        Assert.False(all.ContainsKey("layout"));
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
        Assert.Equal(1.5, store.GetSection("color")!["channelAdjustment"]![0]!["gamma"]!.GetValue<double>());
        Assert.Equal(0, changes);
        Assert.False(File.Exists(Path.Combine(_dir, JsonFileSettingsStore.FileName)));
    }

    [Theory]
    [InlineData("""{ "color": { "imageToLedMappingType": "fancy" } }""", "color.imageToLedMappingType", "must be one of: \"advanced\", \"unicolor_mean\"")]
    [InlineData("""{ "color": { "sparse_processing": "yes" } }""", "color.sparse_processing", "must be true or false")]
    [InlineData("""{ "color": { "bogus": 1 } }""", "color.bogus", "isn't a known setting")]
    [InlineData("""{ "leds": [] }""", "leds", "needs at least 1 item")]
    [InlineData("""{ "leds": [{ "hmin": 0, "hmax": 1.5, "vmin": 0, "vmax": 1 }] }""", "leds.0.hmax", "must be at most 1")]
    [InlineData("""{ "leds": [{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1, "group": 0.5 }] }""", "leds.0.group", "must be a whole number")]
    [InlineData("""{ "smoothing": { "time_ms": 99999 } }""", "smoothing", "isn't a setting this version supports")]
    [InlineData("""{ "device": { "type": "hue" } }""", "device.type", "unknown LED device type \"hue\" (available: file, wled)")]
    [InlineData("""{ "device": { "type": "wled", "host": "x", "brightnessMaxLevel": 300 } }""", "device.brightnessMaxLevel", "must be at most 255")]
    [InlineData("""{ "layout": { "version": 0, "controllers": [], "strips": [] } }""", "layout.version", "must be at least 1")]
    [InlineData("""{ "general": 5 }""", "general", "must be an object")]
    public void Update_RejectsInvalidValues(string update, string path, string message)
    {
        var result = Open().Update(Parse(update));

        Assert.Contains(new SettingsError(path, message), result.Errors);
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

        var reopened = Open();
        Assert.True(JsonNode.DeepEquals(store.Snapshot(), reopened.Snapshot()));
        Assert.True(reopened.GetSection("leds")![1]!["disabled"]!.GetValue<bool>());
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
    public void Update_StoresTheLayoutEnvelopeAsSent()
    {
        var store = Open();

        var result = store.Update(Parse("""{ "layout": { "version": 1, "controllers": [{ "id": "c" }], "strips": [], "extra": true } }"""));

        Assert.True(result.Success);
        Assert.True(store.GetSection("layout")!["extra"]!.GetValue<bool>());
    }

    [Fact]
    public void Load_ResetsInvalidSectionsAndKeepsUnknownOnes()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, JsonFileSettingsStore.FileName), """
            { "version": 1, "settings": {
                "general": { "name": "x" },
                "logger": { "level": "verbose" },
                "futureFeature": { "on": true } } }
            """);

        var store = Open();
        Assert.Equal("My Overglow Config", store.GetSection("general")!["name"]!.GetValue<string>());
        Assert.Equal("verbose", store.GetSection("logger")!["level"]!.GetValue<string>());
        Assert.Null(store.GetSection("futureFeature"));

        store.Update(Parse("""{ "logger": { "level": "warn" } }"""));
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, JsonFileSettingsStore.FileName)))!;
        Assert.True(file["settings"]!["futureFeature"]!["on"]!.GetValue<bool>());
    }

    [Fact]
    public void ClientSchema_ContainsSectionsAndDriverSchemas()
    {
        var schema = Schema().ToClientSchema();
        var properties = schema["properties"]!.AsObject();

        Assert.Contains("color", properties.Select(p => p.Key));
        Assert.Contains("layout", properties.Select(p => p.Key));
        Assert.Equal(["file", "wled"], properties["alldevices"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void AuthStore_RoundTripsHashesAndTokens()
    {
        var store = new JsonFileAuthStore(_dir);
        Assert.Same(AuthData.Empty, store.Load());

        var data = new AuthData(
            new SecretHash([1, 2, 3], [4, 5], 1000),
            false,
            [new ApiToken("a1b2c", "Stream Deck", new SecretHash([9], [8], 10), new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))]);
        store.Save(data);
        var loaded = new JsonFileAuthStore(_dir).Load();

        Assert.Equal([1, 2, 3], loaded.Password!.Hash);
        Assert.Equal(1000, loaded.Password.Iterations);
        Assert.False(loaded.IsDefaultPassword);
        var token = Assert.Single(loaded.Tokens);
        Assert.Equal(("a1b2c", "Stream Deck"), (token.Id, token.Comment));
        Assert.Equal(data.Tokens[0].LastUse, token.LastUse);
    }
}
