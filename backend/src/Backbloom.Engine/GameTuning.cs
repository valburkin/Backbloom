using System.Text.Json.Nodes;
using Backbloom.Capture;

namespace Backbloom.Engine;

/// <summary>Ambient (the lights as the settings say) or Game (the same, with <see cref="GameTuning"/> on top).</summary>
public enum LightMode
{
    Ambient,
    Game,
}

/// <summary>
/// What Game mode changes (the <c>game</c> section): the transition time and its spring, the saturation and color brightness
/// gains (the UI's Intensity), the capture rate, the brightness once it was set in Game mode, and no minimum glow. While
/// Game mode is on these replace the matching values of <c>smoothing</c>, <c>color.channelAdjustment[0]</c> and
/// <c>systemGrabber</c>; those sections stay as saved.
/// </summary>
/// <param name="ScaleOutput">Home's Brightness in Game mode (<c>scaleOutput</c>); null: Ambient's.</param>
public sealed record GameTuning(int TimeMs, int Stiffness, int Damping, double SaturationGain, double LuminanceGain, int FramesPerSecond, double? ScaleOutput = null)
{
    public const string SectionName = "game";

    /// <summary>The section with the chosen mode (<c>modes.mode</c>).</summary>
    public const string ModesSectionName = "modes";

    /// <summary>The schema's defaults: the Intense preset at 60 fps.</summary>
    public static readonly GameTuning Default = new(150, 817, 61, 1.5, 1.2, 60);

    /// <summary>The saved mode (<c>modes.mode</c>), Ambient when missing or unknown.</summary>
    public static LightMode SavedMode(JsonNode? modes) =>
        modes?["mode"] is JsonValue v && v.TryGetValue(out string? s) && s == "game" ? LightMode.Game : LightMode.Ambient;

    public static string ToSettingString(LightMode mode) => mode == LightMode.Game ? "game" : "ambient";

    /// <summary>Reads the <c>game</c> section; missing or mistyped values keep their defaults (the store validates them).</summary>
    public static GameTuning FromJson(JsonNode? game)
    {
        var o = game as JsonObject;
        return new GameTuning(
            Int(o, "time_ms", Default.TimeMs),
            Int(o, "stiffness", Default.Stiffness),
            Int(o, "damping", Default.Damping),
            Number(o, "saturationGain", Default.SaturationGain),
            Number(o, "luminanceGain", Default.LuminanceGain),
            Int(o, "fps", Default.FramesPerSecond),
            o?["scaleOutput"] is JsonValue scale && scale.TryGetValue(out double s) ? s : null);
    }

    /// <summary>A copy of the <c>smoothing</c> section with Game mode's transition time and spring, smoothing on.</summary>
    public JsonObject ApplyToSmoothing(JsonNode? smoothing)
    {
        // as doubles, like parsed numbers: the smoothing settings read every number as one
        var result = smoothing?.DeepClone() as JsonObject ?? [];
        result["enable"] = true;
        result["time_ms"] = (double)TimeMs;
        result["stiffness"] = (double)Stiffness;
        result["damping"] = (double)Damping;
        return result;
    }

    /// <summary>
    /// A copy of the <c>color</c> section whose first channel adjustment has Game mode's gains, its brightness when set,
    /// and no minimum glow (<c>backlightThreshold</c> 0: dark scenes stay dark while playing).
    /// </summary>
    public JsonObject ApplyToColor(JsonNode? color)
    {
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
        first["saturationGain"] = SaturationGain;
        first["backlightThreshold"] = 0.0;
        first["luminanceGain"] = LuminanceGain;
        if (ScaleOutput is { } scale)
        {
            first["scaleOutput"] = scale;
        }

        return result;
    }

    /// <summary>The capture settings with at least Game mode's rate (a faster rate set for both modes stays).</summary>
    public CaptureSettings ApplyTo(CaptureSettings capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return capture.FramesPerSecond >= FramesPerSecond ? capture : capture with { FramesPerSecond = FramesPerSecond };
    }

    private static int Int(JsonObject? o, string name, int fallback) =>
        o?[name] is JsonValue v && v.TryGetValue(out double d) ? (int)Math.Round(d) : fallback;

    private static double Number(JsonObject? o, string name, double fallback) =>
        o?[name] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;
}
