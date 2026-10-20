using System.Text.Json.Nodes;

namespace Overglow.Core.Smoothing;

/// <summary>The interpolators of the <c>smoothing.type</c> setting.</summary>
public enum SmoothingType
{
    Stepper,
    RgbInterpolator,
    YuvInterpolator,
    HybridInterpolator,
    ExponentialInterpolator,
    HybridRgbInterpolator,
}

public static class SmoothingTypes
{
    public static IReadOnlyList<SmoothingType> All { get; } =
    [
        SmoothingType.Stepper, SmoothingType.RgbInterpolator, SmoothingType.YuvInterpolator, SmoothingType.HybridInterpolator,
        SmoothingType.ExponentialInterpolator, SmoothingType.HybridRgbInterpolator,
    ];

    /// <summary>Parses the setting; unknown names are the Stepper (C++ <c>StringToEnumSmoothingType</c>).</summary>
    public static SmoothingType Parse(string? name) => name switch
    {
        "RgbInterpolator" => SmoothingType.RgbInterpolator,
        "YuvInterpolator" => SmoothingType.YuvInterpolator,
        "HybridInterpolator" => SmoothingType.HybridInterpolator,
        "ExponentialInterpolator" => SmoothingType.ExponentialInterpolator,
        "HybridRgbInterpolator" => SmoothingType.HybridRgbInterpolator,
        _ => SmoothingType.Stepper,
    };

    public static string ToSettingString(this SmoothingType type) => type switch
    {
        SmoothingType.RgbInterpolator => "RgbInterpolator",
        SmoothingType.YuvInterpolator => "YuvInterpolator",
        SmoothingType.HybridInterpolator => "HybridInterpolator",
        SmoothingType.ExponentialInterpolator => "ExponentialInterpolator",
        SmoothingType.HybridRgbInterpolator => "HybridRgbInterpolator",
        _ => "Stepper",
    };
}

/// <summary>
/// One smoothing configuration (C++ <c>InfiniteSmoothing::SmoothingConfig</c>): the user's (from the <c>smoothing</c>
/// setting) or one an effect registers.
/// </summary>
/// <param name="Pause">The source shows its colors directly (an effect's "direct mode"): smoothing is bypassed.</param>
/// <param name="SettlingTimeMs">Transition time.</param>
/// <param name="UpdateIntervalMs">Time between two smoothing ticks, i.e. between two frames sent to the controllers.</param>
public sealed record SmoothingConfig(
    bool Pause,
    long SettlingTimeMs,
    long UpdateIntervalMs,
    SmoothingType Type = SmoothingType.Stepper,
    float SmoothingFactor = 0.0f,
    float Stiffness = 0.0f,
    float Damping = 0.0f,
    float YLimit = 0.0f)
{
    public const long DefaultSettlingTimeMs = 200;
    public const double DefaultUpdateFrequency = 25;
    public const double MinimalUpdateFrequency = 20;

    /// <summary>The initial configuration before the settings are read (C++ <c>addConfig(200, 25)</c>).</summary>
    public static SmoothingConfig Initial { get; } = ForEffect(DefaultSettlingTimeMs, DefaultUpdateFrequency, pause: false);

    /// <summary>round(max(1000 / max(frequency, 20), 5)) ms (C++ <c>handleSignalInstanceSettingsChanged</c>).</summary>
    public static long IntervalFromFrequency(double updateFrequency) =>
        (long)Math.Round(Math.Max(1000.0 / Math.Max(updateFrequency, MinimalUpdateFrequency), 5.0), MidpointRounding.AwayFromZero);

    /// <summary>The user configuration from the <c>smoothing</c> setting, with the C++ fallbacks for missing fields.</summary>
    public static SmoothingConfig FromSettings(JsonNode? smoothing)
    {
        var o = smoothing as JsonObject;
        return new SmoothingConfig(
            Pause: false,
            SettlingTimeMs: (long)Number(o, "time_ms", DefaultSettlingTimeMs),
            UpdateIntervalMs: IntervalFromFrequency(Number(o, "updateFrequency", DefaultUpdateFrequency)),
            Type: SmoothingTypes.Parse(o?["type"] is JsonValue t && t.TryGetValue(out string? s) ? s : null),
            SmoothingFactor: (float)Number(o, "smoothingFactor", 0.1),
            Stiffness: (float)Number(o, "stiffness", 200),
            Damping: (float)Number(o, "damping", 26),
            YLimit: (float)Number(o, "y_limit", 0));
    }

    /// <summary>A configuration an effect asks for (C++ <c>addConfig</c>: Stepper, interval truncated).</summary>
    public static SmoothingConfig ForEffect(long settlingTimeMs, double updateFrequency, bool pause) =>
        new(pause, settlingTimeMs, updateFrequency > double.Epsilon ? (long)(1000.0 / updateFrequency) : 10);

    private static double Number(JsonObject? o, string name, double fallback) =>
        o?[name] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;
}

/// <summary>The whole <c>smoothing</c> setting.</summary>
public sealed record SmoothingSettings(bool Enable, bool ContinuousOutput, bool AntiFlickeringFilter, SmoothingConfig Config)
{
    public static SmoothingSettings FromJson(JsonNode? smoothing)
    {
        var o = smoothing as JsonObject;
        return new SmoothingSettings(Flag(o, "enable"), Flag(o, "continuousOutput"), Flag(o, "antiFlickeringFilter"), SmoothingConfig.FromSettings(o));
    }

    // C++ reads every flag with toBool(true).
    private static bool Flag(JsonObject? o, string name) => o?[name] is not JsonValue v || !v.TryGetValue(out bool b) || b;
}
