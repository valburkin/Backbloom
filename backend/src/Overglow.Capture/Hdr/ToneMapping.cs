namespace Overglow.Capture.Hdr;

/// <summary>When the HDR tone mapping table is applied (<c>systemGrabber.hdrToneMapping</c>).</summary>
public enum ToneMappingMode
{
    /// <summary>Only for monitors that Windows drives in HDR mode (the default).</summary>
    Auto,

    /// <summary>For every 8-bit capture, also of SDR monitors (HDR10 content shown without Windows HDR).</summary>
    On,

    /// <summary>Never.</summary>
    Off,
}

/// <summary>The HDR state of the screen capture, for <c>serverinfo.systemGrabbers.hdr</c> and <c>capture-update</c>.</summary>
/// <param name="Mode">The configured mode.</param>
/// <param name="Capturing">A monitor is being captured; the other values describe it (any of them for several).</param>
/// <param name="MonitorHdr">Windows drives a captured monitor in HDR mode.</param>
/// <param name="ToneMapped">HDR colors are converted to SDR: by the HDR capture shader or by the table.</param>
public sealed record CaptureHdrStatus(ToneMappingMode Mode, bool Capturing, bool MonitorHdr, bool ToneMapped)
{
    public static CaptureHdrStatus Idle(ToneMappingMode mode) => new(mode, false, false, false);
}

/// <summary>The tone mapping rules of the screen capture.</summary>
public static class ToneMapping
{
    public const string AutoValue = "auto";
    public const string OnValue = "on";
    public const string OffValue = "off";

    /// <summary>The setting value ("auto", "on", "off"); anything else is <see cref="ToneMappingMode.Auto"/>.</summary>
    public static ToneMappingMode Parse(string? value) => value switch
    {
        OnValue => ToneMappingMode.On,
        OffValue => ToneMappingMode.Off,
        _ => ToneMappingMode.Auto,
    };

    public static string ToSettingString(this ToneMappingMode mode) => mode switch
    {
        ToneMappingMode.On => OnValue,
        ToneMappingMode.Off => OffValue,
        _ => AutoValue,
    };

    /// <summary>
    /// Whether the table applies to a monitor's capture. It converts HDR10 (PQ) colors to SDR and only makes sense for
    /// 8-bit captures: an HDR capture (<paramref name="hdrCapture"/>, FP16 scRGB or 10-bit PQ) is converted by the shader.
    /// Applied to the picture of an SDR monitor (normal SDR content) it oversaturates the colors and shifts them towards
    /// red and magenta, which is why <see cref="ToneMappingMode.Auto"/> applies it only while Windows drives the
    /// monitor in HDR mode (as HyperHDR's on did there).
    /// </summary>
    public static bool AppliesLut(ToneMappingMode mode, bool monitorHdr, bool hdrCapture) =>
        !hdrCapture && mode switch
        {
            ToneMappingMode.On => true,
            ToneMappingMode.Auto => monitorHdr,
            _ => false,
        };
}
