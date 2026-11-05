using System.Text.Json.Nodes;
using Backbloom.Capture;
using Backbloom.Core.Smoothing;
using Backbloom.Settings.Layout;

namespace Backbloom.Engine;

/// <summary>
/// What applies on top of the saved settings without changing them: an active profile's Look
/// (<see cref="ProfileLook"/>, the layout's <c>look</c>) replaces the transition and the color gains of the saved
/// <c>smoothing</c> and <c>color.channelAdjustment[0]</c> sections, and the screen is read at least
/// <see cref="FastFramesPerSecond"/> times a second while the applied transition is fast (<see cref="CaptureFor"/>).
/// </summary>
public static class LookTuning
{
    /// <summary>The capture rate while the applied transition is fast.</summary>
    public const int FastFramesPerSecond = 60;

    /// <summary>Transitions shorter than this (with smoothing on) count as fast: the UI's Vivid and Intense Looks.</summary>
    public const int FastTransitionMs = 300;

    /// <summary>A copy of the <c>smoothing</c> section with the Look's transition time and spring, smoothing on.</summary>
    public static JsonObject ApplyToSmoothing(ProfileLook look, JsonNode? smoothing)
    {
        ArgumentNullException.ThrowIfNull(look);

        // as doubles, like parsed numbers: the smoothing settings read every number as one
        var result = smoothing?.DeepClone() as JsonObject ?? [];
        result["enable"] = true;
        result["time_ms"] = (double)look.TimeMs;
        result["stiffness"] = (double)look.Stiffness;
        result["damping"] = (double)look.Damping;
        return result;
    }

    /// <summary>
    /// A copy of the <c>color</c> section whose first channel adjustment has the Look's saturation and color brightness
    /// gains and minimum glow (<c>backlightThreshold</c>); its brightness (<c>scaleOutput</c>) stays.
    /// </summary>
    public static JsonObject ApplyToColor(ProfileLook look, JsonNode? color)
    {
        ArgumentNullException.ThrowIfNull(look);
        var result = color?.DeepClone() as JsonObject ?? [];
        if (result["channelAdjustment"] is not JsonArray list)
        {
            list = [];
            result["channelAdjustment"] = list;
        }

        if (list.Count == 0)
        {
            list.Add((JsonNode)new JsonObject());
        }
        else if (list[0] is not JsonObject)
        {
            list[0] = new JsonObject();
        }

        var first = (JsonObject)list[0]!;
        first["saturationGain"] = look.SaturationGain;
        first["backlightThreshold"] = look.BacklightThreshold;
        first["luminanceGain"] = look.LuminanceGain;
        return result;
    }

    /// <summary>
    /// The capture settings that apply with the smoothing <paramref name="applied"/>: at least
    /// <see cref="FastFramesPerSecond"/> while smoothing is on with a transition under <see cref="FastTransitionMs"/>
    /// (a fast transition shows a slow capture's steps), else the saved rate. A faster saved rate always stays.
    /// </summary>
    public static CaptureSettings CaptureFor(CaptureSettings saved, SmoothingSettings applied)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(applied);
        var fast = applied.Enable && applied.Config.SettlingTimeMs < FastTransitionMs;
        return !fast || saved.FramesPerSecond >= FastFramesPerSecond ? saved : saved with { FramesPerSecond = FastFramesPerSecond };
    }
}
