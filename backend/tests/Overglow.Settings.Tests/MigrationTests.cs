using System.Text.Json.Nodes;
using Overglow.Settings.Import;
using static Overglow.Settings.Tests.TestSchema;

namespace Overglow.Settings.Tests;

/// <summary>One test per rule of <see cref="LegacyMigrations"/>; each is also run twice to show it is idempotent.</summary>
public sealed class MigrationTests
{
    private static readonly Func<string, bool> s_available = t => t is "file" or "wled";

    private static List<string> Run(Action<JsonObject, List<string>> rule, JsonObject config)
    {
        var changes = new List<string>();
        rule(config, changes);
        var again = new List<string>();
        var once = config.DeepClone();
        rule(config, again);
        Assert.Empty(again);
        Assert.True(JsonNode.DeepEquals(once, config));
        return changes;
    }

    [Fact]
    public void UpgradeFormat_Version1_LowersCountThresholdsThenMapsThemToTheSmallestStep()
    {
        var config = Parse("""
            { "general": { "version": 1, "disableOnLocked": true },
              "color": { "channelAdjustment": [{ "backlightThreshold": 3 }, { "backlightThreshold": 1 }, { "backlightThreshold": 0 }] } }
            """);

        var changes = Run(LegacyMigrations.UpgradeFormat, config);

        Assert.Equal([0.0039, 0.0039, 0.0], config["color"]!["channelAdjustment"]!.AsArray().Select(a => a!["backlightThreshold"]!.GetValue<double>()));
        Assert.Equal(6, config["general"]!["version"]!.GetValue<int>());
        Assert.Equal(["Upgraded the settings format from version 1 to 6"], changes);
    }

    [Fact]
    public void UpgradeFormat_Version4_MapsFractionalThresholds()
    {
        var config = Parse("""{ "general": { "version": 4, "disableOnLocked": true }, "color": { "channelAdjustment": [{ "backlightThreshold": 0.2 }] } }""");

        Run(LegacyMigrations.UpgradeFormat, config);

        Assert.Equal(0.0039, config["color"]!["channelAdjustment"]![0]!["backlightThreshold"]!.GetValue<double>());
    }

    [Fact]
    public void UpgradeFormat_Version5_EnablesPauseWhileLocked()
    {
        var config = Parse("""{ "general": { "version": 5, "disableOnLocked": false } }""");

        var changes = Run(LegacyMigrations.UpgradeFormat, config);

        Assert.True(config["general"]!["disableOnLocked"]!.GetValue<bool>());
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void UpgradeFormat_CurrentOrUnreadableVersion_ChangesNothing()
    {
        Assert.Empty(Run(LegacyMigrations.UpgradeFormat, Parse("""{ "general": { "version": 6, "disableOnLocked": false } }""")));
        Assert.Empty(Run(LegacyMigrations.UpgradeFormat, Parse("""{ "color": { "channelAdjustment": [{ "backlightThreshold": 3 }] } }""")));
    }

    [Fact]
    public void RemoveUnsupportedFeatures_RemovesCecAndSignalDetectionOptions()
    {
        var config = Parse("""
            { "systemControl": { "cecControl": true, "systemInstanceEnable": true },
              "systemGrabber": { "fps": 30, "signalDetection": true, "redSignalThreshold": 5, "sDHOffsetMax": 0.75 } }
            """);

        var changes = Run(LegacyMigrations.RemoveUnsupportedFeatures, config);

        Assert.Equal("""{"systemInstanceEnable":true}""", config["systemControl"]!.ToJsonString());
        Assert.Equal("""{"fps":30}""", config["systemGrabber"]!.ToJsonString());
        Assert.Equal(2, changes.Count);
    }

    [Theory]
    [InlineData("Atomic swirl", "Rainbow swirl")]
    [InlineData("Plasma", "Rainbow swirl")]
    [InlineData("Blue mood blobs", "Full color mood blobs")]
    [InlineData("Red mood blobs", "Warm mood blobs")]
    [InlineData("Cinema dim lights", "Candle")]
    [InlineData("Knight rider", "Breath")]
    [InlineData("Music: equalizer test (turn on video preview)", "Music: fullscreen pulse (MULTI COLOR)")]
    [InlineData("Music: quatro for LED strip (RED)", "Music: quatro for LED strip (MULTI COLOR)")]
    [InlineData("Music: stereo for LED strip (MULTI COLOR FAST)", "Music: stereo for LED strip (MULTI COLOR)")]
    [InlineData("Music: fullscreen pulse (BLUE)", "Music: fullscreen pulse (MULTI COLOR)")]
    [InlineData("Rainbow swirl fast", "Rainbow swirl fast")]
    [InlineData("Candle", "Candle")]
    public void RemoveUnsupportedFeatures_MapsRemovedEffectsToKeptOnes(string effect, string expected)
    {
        var config = new JsonObject
        {
            ["foregroundEffect"] = new JsonObject { ["effect"] = effect },
            ["backgroundEffect"] = new JsonObject { ["effect"] = effect },
        };

        var changes = Run(LegacyMigrations.RemoveUnsupportedFeatures, config);

        Assert.Equal(expected, config["foregroundEffect"]!["effect"]!.GetValue<string>());
        Assert.Equal(expected, config["backgroundEffect"]!["effect"]!.GetValue<string>());
        Assert.Equal(effect == expected ? 0 : 2, changes.Count);
    }

    [Theory]
    [InlineData("ws2812spi")]
    [InlineData("apa102")]
    [InlineData("adalight")]
    public void ReplaceUnavailableDevice_SwitchesToTheFileDriverKeepingColorOrderAndRefresh(string type)
    {
        var config = Parse($$"""{ "device": { "type": "{{type}}", "output": "/dev/spidev0.0", "colorOrder": "grb", "refreshTime": 20, "rate": 1000000 } }""");

        var changes = Run((c, l) => LegacyMigrations.ReplaceUnavailableDevice(c, s_available, l), config);

        Assert.Equal("""{"type":"file","output":"/dev/null","printTimeStamp":false,"colorOrder":"grb","refreshTime":20}""", config["device"]!.ToJsonString());
        Assert.Contains($"'{type}' is not supported", Assert.Single(changes), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceUnavailableDevice_KeepsAvailableDriversAndNormalizesTheirName()
    {
        var config = Parse("""{ "device": { "type": "WLED", "host": "wled.local" } }""");

        Assert.Empty(Run((c, l) => LegacyMigrations.ReplaceUnavailableDevice(c, s_available, l), config));
        Assert.Equal("wled", config["device"]!["type"]!.GetValue<string>());
        Assert.Equal("wled.local", config["device"]!["host"]!.GetValue<string>());
    }

    [Fact]
    public void RenameFactoryName_RenamesOnlyHyperHdrsDefault()
    {
        var config = Parse("""{ "general": { "name": "My HyperHDR Config" } }""");
        Assert.Single(Run(LegacyMigrations.RenameFactoryName, config));
        Assert.Equal("My Overglow Config", config["general"]!["name"]!.GetValue<string>());

        var custom = Parse("""{ "general": { "name": "Living room" } }""");
        Assert.Empty(Run(LegacyMigrations.RenameFactoryName, custom));
        Assert.Equal("Living room", custom["general"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{}""", false)]
    [InlineData("""{ "version": 1, "controllers": [], "strips": [] }""", false)]
    [InlineData("""{ "version": 1, "controllers": [{ "id": "a" }], "strips": [{ "id": "s" }] }""", true)]
    public void DropEmptyLayout_LeavesTheLayoutAbsentUnlessItHasStrips(string layout, bool kept)
    {
        var config = new JsonObject { ["layout"] = JsonNode.Parse(layout) };

        Run(LegacyMigrations.DropEmptyLayout, config);

        Assert.Equal(kept, config.ContainsKey("layout"));
    }

    [Theory]
    [InlineData("true", "auto")]
    [InlineData("false", "off")]
    [InlineData("\"on\"", "on")]
    [InlineData("\"auto\"", "auto")]
    public void ConvertToneMapping_TurnsTheSwitchIntoAModeWhereOnBecomesAuto(string stored, string expected)
    {
        var config = Parse($$"""{ "systemGrabber": { "fps": 30, "hdrToneMapping": {{stored}} } }""");

        var changes = Run((c, list) => LegacyMigrations.Apply(c, s_available, list), config);

        Assert.Equal(expected, config["systemGrabber"]!["hdrToneMapping"]!.GetValue<string>());
        Assert.Equal(stored is "true" or "false", changes.Exists(c => c.StartsWith("HDR tone mapping was", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConvertToneMapping_ImportedSettingsValidateAndLeaveAMissingValueToTheDefault()
    {
        var schema = Create();
        foreach (var stored in new[] { """{ "hdrToneMapping": true }""", """{ "hdrToneMapping": false }""", "{}" })
        {
            var capture = Parse(stored);
            LegacyMigrations.ConvertToneMapping(capture, []);
            var fixes = new List<SettingsError>();

            var repaired = schema.Repair("systemGrabber", capture, fixes)!;

            Assert.DoesNotContain(fixes, f => f.Path.Contains("hdrToneMapping", StringComparison.Ordinal));
            Assert.True(repaired["hdrToneMapping"]!.GetValue<string>() is "auto" or "off");
        }

        Assert.Equal("auto", schema.CreateDefault("systemGrabber")!["hdrToneMapping"]!.GetValue<string>());
    }
}
