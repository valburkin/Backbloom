using System.Text.Encodings.Web;
using System.Text.Json;

namespace Overglow.Devices.Drivers;

/// <summary>Color conversions of the lamp drivers (Home Assistant, Zigbee2MQTT, Yeelight), bit-exact with the C++ code.</summary>
public static class LampColor
{
    /// <summary>std::numeric_limits&lt;float&gt;::epsilon().</summary>
    private const float FloatEpsilon = 1.1920929E-07f;

    /// <summary>JSON as QJsonDocument writes it: compact, non-ASCII characters unescaped.</summary>
    public static readonly JsonSerializerOptions QtJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Port of <c>ColorRgb::rgb2hsl</c> (sources/image/ColorRgb.cpp): hue 0..359, saturation and luminance 0..1.</summary>
    public static (ushort Hue, float Saturation, float Luminance) RgbToHsl(byte red, byte green, byte blue)
    {
        var r = red / 255.0f;
        var g = green / 255.0f;
        var b = blue / 255.0f;
        var min = r < g ? (r < b ? r : b) : (g < b ? g : b);
        var max = r > g ? (r > b ? r : b) : (g > b ? g : b);
        var diff = max - min;
        var luminance = (min + max) / 2.0f;
        if (diff == 0.0f)
        {
            return (0, 0.0f, luminance);
        }

        var saturation = luminance < 0.5f ? diff / (min + max) : diff / (2.0f - min - max);
        ushort hue;
        if (max == r)
        {
            hue = (ushort)(360 + (60 * (g - b) / (max - min)));
            if (hue > 359)
            {
                hue -= 360;
            }
        }
        else if (max == g)
        {
            hue = (ushort)(120 + (60 * (b - r) / (max - min)));
        }
        else
        {
            hue = (ushort)(240 + (60 * (r - g) / (max - min)));
        }

        return (hue, saturation, luminance);
    }

    /// <summary>
    /// Port of <c>ColorRgb::rgb2hsv</c>: hue 0..360 (rounded degrees), saturation and value 0..255; the C++ code mixes
    /// float and double, which is kept.
    /// </summary>
    public static (int Hue, int Saturation, int Value) RgbToHsv(byte red, byte green, byte blue)
    {
        var r = (float)(red / 255.0);
        var g = (float)(green / 255.0);
        var b = (float)(blue / 255.0);
        var max = Math.Max(Math.Max(r, g), b);
        var min = Math.Min(Math.Min(r, g), b);
        var delta = max - min;
        var value = Clamp(Math.Round(max * 255.0, MidpointRounding.AwayFromZero));
        if (delta < FloatEpsilon || max <= FloatEpsilon)
        {
            return (0, 0, value);
        }

        var s = delta / max;
        float h;
        if (r >= max)
        {
            h = (g - b) / delta;
        }
        else if (g >= max)
        {
            h = (float)(2.0 + ((b - r) / delta));
        }
        else
        {
            h = (float)(4.0 + ((r - g) / delta));
        }

        h = (float)(h * 60.0);
        while (h < 0.0)
        {
            h = (float)(h + 360.0);
        }

        while (h > 360.0)
        {
            h = (float)(h - 360.0);
        }

        return ((ushort)Math.Round(h, MidpointRounding.AwayFromZero), Clamp(Math.Round(s * 255, MidpointRounding.AwayFromZero)), value);

        static int Clamp(double v) => (int)Math.Clamp(v, 0, 255);
    }

    /// <summary>The perceived brightness the lamp drivers send with RGB colors: round(0.2126 R + 0.7152 G + 0.0722 B).</summary>
    public static int Luma(byte r, byte g, byte b) =>
        Math.Clamp((int)Math.Round((0.2126 * r) + (0.7152 * g) + (0.0722 * b), MidpointRounding.AwayFromZero), 0, 255);
}
