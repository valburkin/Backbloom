using System.Text.Json.Nodes;
using Backbloom.Capture;
using Backbloom.Settings;

namespace Backbloom.Engine.Tests;

public sealed class GameTuningTests
{
    [Fact]
    public void Default_IsTheSchemasDefault()
    {
        var schema = SettingsSchema.CreateBuiltin(new Dictionary<string, JsonObject>());
        Assert.Equal(GameTuning.Default, GameTuning.FromJson(schema.CreateDefault(GameTuning.SectionName)));
        Assert.Equal(LightMode.Ambient, GameTuning.SavedMode(schema.CreateDefault(GameTuning.ModesSectionName)));
        Assert.Equal(GameTuning.Default, GameTuning.FromJson(null));
    }

    [Fact]
    public void ApplyToSmoothing_TurnsSmoothingOn_WithItsTimeAndSpring_AndKeepsTheRest()
    {
        var smoothing = new JsonObject { ["enable"] = false, ["type"] = "HybridRgbInterpolator", ["time_ms"] = 350, ["stiffness"] = 150, ["damping"] = 26 };

        var tuned = GameTuning.Default.ApplyToSmoothing(smoothing);

        Assert.Equal("""{"enable":true,"type":"HybridRgbInterpolator","time_ms":150,"stiffness":817,"damping":61}""", tuned.ToJsonString());
        Assert.Equal(350, smoothing["time_ms"]!.GetValue<int>());
    }

    [Fact]
    public void ApplyToColor_SetsTheGainsOfTheFirstAdjustment_WithoutGlow_EvenWithoutOne()
    {
        var color = JsonNode.Parse("""{ "imageToLedMappingType": "advanced", "channelAdjustment": [{ "gamma": 2, "saturationGain": 1 }, { "gamma": 1 }] }""");

        var tuned = GameTuning.Default.ApplyToColor(color);

        Assert.Equal(
            """{"imageToLedMappingType":"advanced","channelAdjustment":[{"gamma":2,"saturationGain":1.5,"backlightThreshold":0,"luminanceGain":1.2},{"gamma":1}]}""",
            tuned.ToJsonString());
        Assert.Equal("""{"channelAdjustment":[{"saturationGain":1.5,"backlightThreshold":0,"luminanceGain":1.2}]}""", GameTuning.Default.ApplyToColor(null).ToJsonString());
        Assert.Equal(
            """{"channelAdjustment":[{"saturationGain":1.5,"backlightThreshold":0,"luminanceGain":1.2}]}""",
            GameTuning.Default.ApplyToColor(JsonNode.Parse("""{ "channelAdjustment": [3] }""")).ToJsonString());
    }

    [Fact]
    public void ApplyToCapture_RaisesTheRate_ButKeepsAFasterOne()
    {
        var capture = new CaptureSettings { FramesPerSecond = 30, Device = "auto" };

        Assert.Equal(capture with { FramesPerSecond = 60 }, GameTuning.Default.ApplyTo(capture));
        var fast = capture with { FramesPerSecond = 90 };
        Assert.Same(fast, GameTuning.Default.ApplyTo(fast));
    }
}
