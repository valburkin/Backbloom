using System.Text.Json.Nodes;
using Backbloom.Capture.Hdr;

namespace Backbloom.Capture;

/// <summary>Pixels ignored at each edge of the captured screen, in screen pixels.</summary>
public readonly record struct CropMargins(int Left, int Right, int Top, int Bottom);

/// <summary>
/// The <c>systemGrabber</c> settings section (screen capture), with HyperHDR's names and defaults. Ported from
/// SystemWrapper::handleSettingsUpdate and schema-systemGrabber.json.
/// </summary>
public sealed record CaptureSettings
{
    public const string SectionName = "systemGrabber";

    /// <summary>Device value that selects the first monitor (in sorted order, as HyperHDR did).</summary>
    public const string AutoDevice = "auto";

    /// <summary>Monitor to capture: "auto", a monitor id (see <see cref="MonitorInfo"/>) or a multi-monitor id.</summary>
    public string Device { get; init; } = AutoDevice;

    /// <summary>Maximum width of the delivered frame (<c>videoMode</c>); the screen is scaled down by a whole factor.</summary>
    public int MaxWidth { get; init; } = 512;

    public int FramesPerSecond { get; init; } = 30;

    /// <summary>When the HDR to SDR lookup table is applied to 8-bit captures (see <see cref="Hdr.ToneMapping.AppliesLut"/>).</summary>
    public ToneMappingMode ToneMapping { get; init; }

    /// <summary>Scale and convert on the GPU; off captures full-size frames and scales them on the CPU.</summary>
    public bool Hardware { get; init; } = true;

    /// <summary>
    /// Take the SDR white level of an HDR desktop from Windows ("SDR content brightness" of the monitor, read when the
    /// capture starts and watched while it runs) instead of <see cref="MonitorNits"/> (<c>monitor_nits_auto</c>, a
    /// Backbloom addition). Where Windows doesn't report one, <see cref="MonitorNits"/> applies.
    /// </summary>
    public bool MonitorNitsAuto { get; init; } = true;

    /// <summary>
    /// SDR white level of an HDR desktop set by hand (Windows' "SDR content brightness" in nits); 0 disables the HDR
    /// capture path so the desktop is captured as 8-bit SDR, converted by Windows.
    /// </summary>
    public int MonitorNits { get; init; } = 200;

    /// <summary>Permutation index of the monitor order in multi-monitor capture (0 = left to right).</summary>
    public int ReorderDisplays { get; init; }

    public CropMargins Crop { get; init; }

    /// <summary>
    /// The SDR white level an HDR desktop is converted with: Windows' level (<paramref name="windowsNits"/>, 0 when
    /// unknown) with <see cref="MonitorNitsAuto"/>, else <see cref="MonitorNits"/>. 0 means no HDR capture.
    /// </summary>
    public int SdrWhiteNits(int windowsNits) => MonitorNitsAuto && windowsNits > 0 ? windowsNits : MonitorNits;

    /// <summary>Reads the section; missing or mistyped values keep their defaults (the store validates them).</summary>
    public static CaptureSettings FromJson(JsonNode? section)
    {
        var o = section as JsonObject;
        var defaults = new CaptureSettings();
        return new CaptureSettings
        {
            Device = GetString(o, "device") is { Length: > 0 } device ? device : AutoDevice,
            MaxWidth = Math.Max(1, GetInt(o, "videoMode", defaults.MaxWidth)),
            FramesPerSecond = Math.Clamp(GetInt(o, "fps", defaults.FramesPerSecond), 1, 240),
            ToneMapping = Hdr.ToneMapping.Parse(GetString(o, "hdrToneMapping")),
            Hardware = GetBool(o, "hardware", defaults.Hardware),
            MonitorNitsAuto = GetBool(o, "monitor_nits_auto", defaults.MonitorNitsAuto),
            MonitorNits = Math.Max(0, GetInt(o, "monitor_nits", defaults.MonitorNits)),
            ReorderDisplays = Math.Max(0, GetInt(o, "reorder_displays", 0)),
            Crop = new CropMargins(
                Math.Max(0, GetInt(o, "cropLeft", 0)),
                Math.Max(0, GetInt(o, "cropRight", 0)),
                Math.Max(0, GetInt(o, "cropTop", 0)),
                Math.Max(0, GetInt(o, "cropBottom", 0))),
        };
    }

    private static string? GetString(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static int GetInt(JsonObject? o, string name, int fallback) =>
        o?[name] is JsonValue v && v.TryGetValue(out double d) ? (int)d : fallback;

    private static bool GetBool(JsonObject? o, string name, bool fallback) =>
        o?[name] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
}
