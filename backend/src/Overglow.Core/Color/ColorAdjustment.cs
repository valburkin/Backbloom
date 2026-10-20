using System.Numerics;
using System.Text.Json.Nodes;

namespace Overglow.Core.Color;

public enum TemperaturePreset
{
    Disabled,
    Cold,
    Neutral,
    Warm,
    Custom,
}

/// <summary>
/// User color adjustments: one entry of the <c>color.channelAdjustment</c> setting. Values are taken as they are;
/// range validation happens at the settings boundary. Field semantics follow
/// <c>InfiniteProcessing::updateCurrentConfig</c> (sources/infinite-color-engine/InfiniteProcessing.cpp).
/// </summary>
public sealed record ColorAdjustment
{
    public static ColorAdjustment Default { get; } = new();

    public TemperaturePreset Temperature { get; init; } = TemperaturePreset.Disabled;

    /// <summary>Per-channel tint used when <see cref="Temperature"/> is <see cref="TemperaturePreset.Custom"/>.</summary>
    public Vector3 CustomTemperature { get; init; } = Vector3.One;

    /// <summary>True: calibrate with the three primaries only (matrix); false: all eight corners (3D LUT).</summary>
    public bool ClassicConfig { get; init; } = true;

    public Rgb8 Red { get; init; } = new(255, 0, 0);

    public Rgb8 Green { get; init; } = new(0, 255, 0);

    public Rgb8 Blue { get; init; } = new(0, 0, 255);

    public Rgb8 Cyan { get; init; } = new(0, 255, 255);

    public Rgb8 Magenta { get; init; } = new(255, 0, 255);

    public Rgb8 Yellow { get; init; } = new(255, 255, 0);

    public Rgb8 White { get; init; } = Rgb8.White;

    public Rgb8 Black { get; init; } = Rgb8.Black;

    /// <summary>Brightness in linear light, 0..2 (1 = unchanged).</summary>
    public float ScaleOutput { get; init; } = 1.0f;

    public float Gamma { get; init; } = 1.5f;

    public float LuminanceGain { get; init; } = 1.0f;

    public float SaturationGain { get; init; } = 1.0f;

    /// <summary>Minimal output level (0 = off).</summary>
    public float BacklightThreshold { get; init; }

    public bool BacklightColored { get; init; } = true;

    /// <summary>Maximum average output of the whole strip, 0..1 (1 = no limit).</summary>
    public float PowerLimit { get; init; } = 1.0f;

    /// <summary>
    /// Reads one channelAdjustment entry. Missing values take the C++ fallbacks (InfiniteProcessing.cpp): e.g. gamma 1.5,
    /// backlightThreshold 0.
    /// </summary>
    /// <remarks>
    /// Missing calibration colors take their ideal value (red = 255,0,0 and so on), so an entry that sets only some of
    /// them calibrates only those. Deliberate deviation from HyperHDR, which read a missing color as black and so
    /// turned that primary or corner off. The settings schema always stores all eight, so this only matters for
    /// hand-made settings and API clients.
    /// </remarks>
    public static ColorAdjustment FromJson(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return Default;
        }

        var d = Default;
        return new ColorAdjustment
        {
            Temperature = ParseTemperature(ReadString(o, "temperatureSetting")),
            CustomTemperature = new Vector3(
                ReadFloat(o, "temperatureRed", 1.0f),
                ReadFloat(o, "temperatureGreen", 1.0f),
                ReadFloat(o, "temperatureBlue", 1.0f)),
            ClassicConfig = ReadBool(o, "classic_config", true),
            Red = Rgb8.FromJson(o["red"], d.Red),
            Green = Rgb8.FromJson(o["green"], d.Green),
            Blue = Rgb8.FromJson(o["blue"], d.Blue),
            Cyan = Rgb8.FromJson(o["cyan"], d.Cyan),
            Magenta = Rgb8.FromJson(o["magenta"], d.Magenta),
            Yellow = Rgb8.FromJson(o["yellow"], d.Yellow),
            White = Rgb8.FromJson(o["white"], d.White),
            Black = Rgb8.FromJson(o["black"], d.Black),
            ScaleOutput = ReadFloat(o, "scaleOutput", 1.0f),
            Gamma = ReadFloat(o, "gamma", 1.5f),
            LuminanceGain = ReadFloat(o, "luminanceGain", 1.0f),
            SaturationGain = ReadFloat(o, "saturationGain", 1.0f),
            BacklightThreshold = ReadFloat(o, "backlightThreshold", 0.0f),
            BacklightColored = ReadBool(o, "backlightColored", true),
            PowerLimit = ReadFloat(o, "powerLimit", 1.0f),
        };
    }

    public static TemperaturePreset ParseTemperature(string? value) => value switch
    {
        "cold" => TemperaturePreset.Cold,
        "neutral" => TemperaturePreset.Neutral,
        "warm" => TemperaturePreset.Warm,
        "custom" => TemperaturePreset.Custom,
        _ => TemperaturePreset.Disabled,
    };

    private static string? ReadString(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static float ReadFloat(JsonObject o, string name, float fallback) =>
        o[name] is JsonValue v && v.TryGetValue(out double d) ? (float)d : fallback;

    private static bool ReadBool(JsonObject o, string name, bool fallback) =>
        o[name] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
}
