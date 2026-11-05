using System.Text.Json.Nodes;
using Backbloom.Capture;
using Backbloom.Core.Smoothing;
using Backbloom.Settings.Layout;

namespace Backbloom.Engine.Tests;

public sealed class LookTuningTests
{
    // The UI's Intense Look (ui/src/settings/intensity.ts) with springFor(150).
    private static readonly ProfileLook s_intense = new(150, 817, 61, 1.5, 1.2, 0);

    [Fact]
    public void ApplyToSmoothing_TurnsSmoothingOn_WithItsTimeAndSpring_AndKeepsTheRest()
    {
        var smoothing = new JsonObject { ["enable"] = false, ["type"] = "HybridRgbInterpolator", ["time_ms"] = 350, ["stiffness"] = 150, ["damping"] = 26 };

        var tuned = LookTuning.ApplyToSmoothing(s_intense, smoothing);

        Assert.Equal("""{"enable":true,"type":"HybridRgbInterpolator","time_ms":150,"stiffness":817,"damping":61}""", tuned.ToJsonString());
        Assert.Equal(350, smoothing["time_ms"]!.GetValue<int>());
    }

    [Fact]
    public void ApplyToColor_SetsTheGainsAndGlowOfTheFirstAdjustment_EvenWithoutOne_AndKeepsTheBrightness()
    {
        var color = JsonNode.Parse("""{ "imageToLedMappingType": "advanced", "channelAdjustment": [{ "gamma": 2, "saturationGain": 1, "scaleOutput": 0.8 }, { "gamma": 1 }] }""");

        var tuned = LookTuning.ApplyToColor(s_intense with { BacklightThreshold = 0.0039 }, color);

        Assert.Equal(
            """{"imageToLedMappingType":"advanced","channelAdjustment":[{"gamma":2,"saturationGain":1.5,"scaleOutput":0.8,"backlightThreshold":0.0039,"luminanceGain":1.2},{"gamma":1}]}""",
            tuned.ToJsonString());
        Assert.Equal(1, color!["channelAdjustment"]![0]!["saturationGain"]!.GetValue<double>());
        Assert.Equal("""{"channelAdjustment":[{"saturationGain":1.5,"backlightThreshold":0,"luminanceGain":1.2}]}""", LookTuning.ApplyToColor(s_intense, null).ToJsonString());
        Assert.Equal(
            """{"channelAdjustment":[{"saturationGain":1.5,"backlightThreshold":0,"luminanceGain":1.2}]}""",
            LookTuning.ApplyToColor(s_intense, JsonNode.Parse("""{ "channelAdjustment": [3] }""")).ToJsonString());
    }

    [Theory]
    [InlineData(true, 150, 30, 60)]
    [InlineData(true, 299, 30, 60)]
    [InlineData(true, 300, 30, 30)]
    [InlineData(true, 350, 30, 30)]
    [InlineData(false, 150, 30, 30)]
    [InlineData(true, 150, 90, 90)]
    public void CaptureFor_Reads60TimesASecond_WhileTransitionsAreFast(bool enable, int timeMs, int savedFps, int expectedFps)
    {
        var saved = new CaptureSettings { FramesPerSecond = savedFps, Device = "auto" };
        var smoothing = SmoothingSettings.FromJson(new JsonObject { ["enable"] = enable, ["time_ms"] = (double)timeMs });

        var applied = LookTuning.CaptureFor(saved, smoothing);

        Assert.Equal(saved with { FramesPerSecond = expectedFps }, applied);
        if (expectedFps == savedFps)
        {
            Assert.Same(saved, applied);
        }
    }
}
