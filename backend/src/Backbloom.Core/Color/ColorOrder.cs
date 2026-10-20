namespace Backbloom.Core.Color;

/// <summary>Byte order a controller expects for each LED. Port of <c>LedString::ColorOrder</c>.</summary>
public enum ColorOrder
{
    Rgb,
    Rbg,
    Grb,
    Brg,
    Gbr,
    Bgr,
}

public static class ColorOrders
{
    /// <summary>Parses "rgb", "grb", ... ; unknown or missing values fall back to RGB like the C++ code.</summary>
    public static ColorOrder Parse(string? value) => value switch
    {
        "bgr" => ColorOrder.Bgr,
        "rbg" => ColorOrder.Rbg,
        "brg" => ColorOrder.Brg,
        "gbr" => ColorOrder.Gbr,
        "grb" => ColorOrder.Grb,
        _ => ColorOrder.Rgb,
    };

    public static string ToSettingString(this ColorOrder order) => order switch
    {
        ColorOrder.Rbg => "rbg",
        ColorOrder.Grb => "grb",
        ColorOrder.Brg => "brg",
        ColorOrder.Gbr => "gbr",
        ColorOrder.Bgr => "bgr",
        _ => "rgb",
    };
}
