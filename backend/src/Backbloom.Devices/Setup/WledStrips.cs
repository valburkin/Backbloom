namespace Backbloom.Devices.Setup;

/// <summary>A strip type the guided setup offers, and how WLED drives it.</summary>
/// <param name="Id">The preset id the UI sends (<c>strip</c>).</param>
/// <param name="WledType">WLED's bus type (<c>hw.led.ins[].type</c>, WLED's const.h <c>TYPE_*</c>).</param>
/// <param name="ClockPin">Two-wire (SPI) strips: data and clock pin.</param>
/// <param name="ColorOrder">The order most strips of this type use, a starting point the color check corrects.</param>
/// <param name="Ws2815Current">WLED's WS2815 current model (LED current 255: 12 mA per LED, white from the brightest channel).</param>
public sealed record WledStrip(string Id, int WledType, bool ClockPin, string ColorOrder, bool Ws2815Current = false);

/// <summary>
/// The strip presets and color orders of the <c>hardware wled-*</c> subcommands and their WLED numbers (WLED's
/// const.h: bus types, <c>COL_ORDER_*</c>; the API values only, no WLED code).
/// </summary>
public static class WledStrips
{
    /// <summary>WLED's "WS281x" bus type, its default.</summary>
    public const int Ws281x = 22;

    /// <summary>The LED current WLED uses as the WS2815 marker.</summary>
    public const int Ws2815Marker = 255;

    /// <summary>What a WS2815 draws per LED at full white (WLED's model for the marker).</summary>
    public const int Ws2815Milliamps = 12;

    /// <summary>WLED's default LED current (a WS2812B at full white, about 55 mA).</summary>
    public const int DefaultLedMilliamps = 55;

    /// <summary>The presets, the common ones first.</summary>
    public static IReadOnlyList<WledStrip> All { get; } =
    [
        new("ws2812b", Ws281x, false, "GRB"),
        new("sk6812-rgbw", 30, false, "GRB"),
        new("ws2811-12v", Ws281x, false, "BRG"),
        new("ws2815", Ws281x, false, "GRB", Ws2815Current: true),
        new("ws2814-rgbw", 30, false, "RGB"),
        new("ws2813", Ws281x, false, "GRB"),
        new("sk6812", Ws281x, false, "GRB"),
        // addressable COB strips: the same ICs, each driving a segment of many chips (WLED counts one LED per segment)
        new("ws2811-cob", Ws281x, false, "RGB"),
        new("ws2812-cob", Ws281x, false, "GRB"),
        new("ws2814-cob-rgbw", 30, false, "RGB"),
        new("ws2805", 32, false, "RGB"),
        new("tm1814", 31, false, "RGB"),
        new("gs8208", 23, false, "RGB"),
        new("ucs8903", 26, false, "RGB"),
        new("ucs8904", 29, false, "RGB"),
        new("apa106", 27, false, "RGB"),
        new("ws2811-400khz", 24, false, "RGB"),
        new("apa102", 51, true, "BGR"),
        new("sk9822", 51, true, "BGR"),
        new("ws2801", 50, true, "RGB"),
        new("lpd8806", 52, true, "GRB"),
        new("p9813", 53, true, "BGR"),
    ];

    /// <summary>Color orders by WLED's number (<c>hw.led.ins[].order</c>, low four bits).</summary>
    public static IReadOnlyList<string> ColorOrders { get; } = ["GRB", "RGB", "BRG", "RBG", "BGR", "GBR"];

    public static WledStrip? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The preset for a WLED bus type (the first one, so WS281x reads as ws2812b), or null for others.</summary>
    public static WledStrip? ForType(int wledType, int ledMilliamps) =>
        wledType == Ws281x && ledMilliamps == Ws2815Marker ? Find("ws2815") : All.FirstOrDefault(s => s.WledType == wledType);

    /// <summary>WLED's number of a color order ("GRB" -> 0), or -1.</summary>
    public static int OrderNumber(string? name)
    {
        for (var i = 0; i < ColorOrders.Count; i++)
        {
            if (string.Equals(ColorOrders[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The name of WLED's color order (its low four bits), or null when it's none of the six.</summary>
    public static string? OrderName(int order) => (order & 0x0F) is var n && n < ColorOrders.Count ? ColorOrders[n] : null;

    /// <summary>Two-wire (SPI) bus types: data and clock pin (WLED's 48..63).</summary>
    public static bool IsTwoWire(int wledType) => wledType is >= 48 and <= 63;
}
