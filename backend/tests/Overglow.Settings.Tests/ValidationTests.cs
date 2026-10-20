using System.Text.Json.Nodes;
using static Overglow.Settings.Tests.TestSchema;

namespace Overglow.Settings.Tests;

/// <summary>setconfig rejects invalid values of every settings type with per-field errors (backend bug 1).</summary>
public sealed class ValidationTests
{
    private static readonly SettingsSchema s_schema = Create();

    public static TheoryData<string> Sections => [.. SettingsSchema.BuiltinSections.Where(s => !SettingsSchema.IsOptional(s))];

    [Theory]
    [MemberData(nameof(Sections))]
    public void Defaults_ComeFromTheSchemasAndAreValid(string section)
    {
        var errors = new List<SettingsError>();

        var value = s_schema.Validate(section, s_schema.CreateDefault(section), errors);

        Assert.Empty(errors);
        Assert.True(JsonNode.DeepEquals(s_schema.CreateDefault(section), value));
    }

    [Theory]
    [InlineData("general", """{ "name": "ab" }""", "general.name", "must be at least 4 characters long")]
    [InlineData("general", """{ "disableOnLocked": 1 }""", "general.disableOnLocked", "must be true or false")]
    [InlineData("logger", """{ "level": "loud" }""", "logger.level", "must be one of: \"silent\", \"warn\", \"verbose\", \"debug\"")]
    [InlineData("device", """{ "type": "hue" }""", "device.type", "unknown LED device type \"hue\" (available: file, wled)")]
    [InlineData("device", """{ "type": "wled", "host": "x", "brightnessMaxLevel": 300 }""", "device.brightnessMaxLevel", "must be at most 255")]
    [InlineData("device", """{ "type": "file", "colorOrder": "xyz" }""", "device.colorOrder", "must be one of: \"rgb\", \"bgr\", \"rbg\", \"brg\", \"gbr\", \"grb\"")]
    [InlineData("color", """{ "imageToLedMappingType": "fancy" }""", "color.imageToLedMappingType", "must be one of: \"advanced\", \"unicolor_mean\"")]
    [InlineData("color", """{ "channelAdjustment": [{ "gamma": 250 }] }""", "color.channelAdjustment.0.gamma", "must be at most 100")]
    [InlineData("color", """{ "channelAdjustment": [{ "red": [255, 0] }] }""", "color.channelAdjustment.0.red", "needs at least 3 items")]
    [InlineData("color", """{ "bogus": 1 }""", "color.bogus", "isn't a known setting")]
    [InlineData("smoothing", """{ "time_ms": 99999 }""", "smoothing.time_ms", "must be at most 5000")]
    [InlineData("smoothing", """{ "type": "Linear" }""", "smoothing.type", "must be one of: \"Stepper\", \"YuvInterpolator\", \"RgbInterpolator\", \"HybridInterpolator\", \"HybridRgbInterpolator\", \"ExponentialInterpolator\"")]
    [InlineData("systemGrabber", """{ "fps": 500 }""", "systemGrabber.fps", "must be at most 120")]
    [InlineData("systemGrabber", """{ "signalDetection": true }""", "systemGrabber.signalDetection", "isn't a known setting")]
    [InlineData("blackborderdetector", """{ "threshold": 101 }""", "blackborderdetector.threshold", "must be at most 100")]
    [InlineData("foregroundEffect", """{ "duration_ms": -2 }""", "foregroundEffect.duration_ms", "must be at least -1")]
    [InlineData("backgroundEffect", """{ "color": [255, 0, 256] }""", "backgroundEffect.color.2", "must be at most 255")]
    [InlineData("soundEffect", """{ "enable": "on" }""", "soundEffect.enable", "must be true or false")]
    [InlineData("webConfig", """{ "port": 70000 }""", "webConfig.port", "must be at most 65535")]
    [InlineData("effects", """{ "paths": [] }""", "effects.paths", "needs at least 1 item")]
    [InlineData("systemControl", """{ "systemInstancePriority": 254 }""", "systemControl.systemInstancePriority", "must be at most 253")]
    [InlineData("systemControl", """{ "cecControl": false }""", "systemControl.cecControl", "isn't a known setting")]
    [InlineData("network", """{ "remoteAccess": "yes" }""", "network.remoteAccess", "must be true or false")]
    [InlineData("network", """{ "localAdminAuth": false }""", "network.localAdminAuth", "isn't a known setting")]
    [InlineData("ledConfig", """{ "classic": { "hdepth": 0 } }""", "ledConfig.classic.hdepth", "must be at least 1")]
    [InlineData("leds", """[]""", "leds", "needs at least 1 item")]
    [InlineData("leds", """[{ "hmin": 0, "hmax": 1.5, "vmin": 0, "vmax": 1 }]""", "leds.0.hmax", "must be at most 1")]
    [InlineData("leds", """[{ "hmin": 0, "hmax": 1, "vmin": 0, "vmax": 1, "group": 0.5 }]""", "leds.0.group", "must be a whole number")]
    [InlineData("mqtt", """{ "port": 80 }""", "mqtt.port", "must be at least 1024")]
    [InlineData("layout", """{ "version": 0, "controllers": [], "strips": [] }""", "layout.version", "must be at least 1")]
    [InlineData("general", "5", "general", "must be an object")]
    public void Update_RejectsInvalidValuesOfEveryType(string section, string json, string path, string message)
    {
        using var dir = new TempDirectory();
        using var open = OpenStore.In(dir.Path, s_schema);
        var before = open.Store.Snapshot();

        var result = open.Store.Update(new JsonObject { [section] = JsonNode.Parse(json) });

        Assert.False(result.Success);
        Assert.Contains(new SettingsError(path, message), result.Errors);
        Assert.True(JsonNode.DeepEquals(before, open.Store.Snapshot()));
    }

    [Fact]
    public void Update_RejectsUnknownSections()
    {
        using var dir = new TempDirectory();
        using var open = OpenStore.In(dir.Path, s_schema);

        var result = open.Store.Update(Parse("""{ "flatbufServer": { "enable": true } }"""));

        Assert.Equal([new SettingsError("flatbufServer", "isn't a setting this version supports")], result.Errors);
    }

    [Fact]
    public void Repair_ClampsResetsAndDropsButOnlyForImports()
    {
        var fixes = new List<SettingsError>();
        var value = Parse("""
            { "type": "Linear", "time_ms": 99999, "updateFrequency": 10, "enable": "yes", "y_limit": 0.5, "unknown": 1 }
            """);

        var repaired = s_schema.Repair("smoothing", value, fixes)!.AsObject();

        Assert.Equal("HybridRgbInterpolator", repaired["type"]!.GetValue<string>());
        Assert.Equal(5000, repaired["time_ms"]!.GetValue<int>());
        Assert.Equal(20, repaired["updateFrequency"]!.GetValue<double>());
        Assert.True(repaired["enable"]!.GetValue<bool>());
        Assert.Equal(0.5, repaired["y_limit"]!.GetValue<double>());
        Assert.False(repaired.ContainsKey("unknown"));
        Assert.Equal(["smoothing.enable", "smoothing.time_ms", "smoothing.type", "smoothing.unknown", "smoothing.updateFrequency"],
            fixes.Select(f => f.Path).Distinct().Order(StringComparer.Ordinal));
        Assert.Empty(ValidationErrors("smoothing", repaired));
    }

    private static List<SettingsError> ValidationErrors(string section, JsonNode value)
    {
        var errors = new List<SettingsError>();
        s_schema.Validate(section, value, errors);
        return errors;
    }
}
