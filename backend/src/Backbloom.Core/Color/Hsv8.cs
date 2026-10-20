namespace Backbloom.Core.Color;

/// <summary>
/// 8-bit RGB/HSV conversions (hue 0..360, saturation and value 0..255) with the float and double steps of
/// <c>ColorRgb::rgb2hsv</c> / <c>hsv2rgb</c> (sources/image/ColorRgb.cpp), which the effects use.
/// </summary>
public static class Hsv8
{
    /// <summary>std::numeric_limits&lt;float&gt;::epsilon().</summary>
    private const float FloatEpsilon = 1.1920929E-07f;

    public static void FromRgb(Rgb8 color, out ushort hue, out byte saturation, out byte value)
    {
        var r = (float)(color.R / 255.0);
        var g = (float)(color.G / 255.0);
        var b = (float)(color.B / 255.0);
        var max = MathF.Max(MathF.Max(r, g), b);
        var min = MathF.Min(MathF.Min(r, g), b);
        var delta = max - min;

        value = Clamp(Math.Round(max * 255.0, MidpointRounding.AwayFromZero));
        if (delta < FloatEpsilon || max <= FloatEpsilon)
        {
            saturation = 0;
            hue = 0;
            return;
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

        hue = (ushort)MathF.Round(h, MidpointRounding.AwayFromZero);
        saturation = Clamp(MathF.Round(s * 255, MidpointRounding.AwayFromZero));
    }

    public static Rgb8 ToRgb(ushort hue, byte saturation, byte value)
    {
        float hh = hue;
        var ins = (float)(saturation / 255.0);
        var inv = (float)(value / 255.0);
        float outR, outG, outB;
        if (ins == 0)
        {
            outR = outG = outB = inv;
        }
        else
        {
            while (hh > 360.0)
            {
                hh = (float)(hh - 360.0);
            }

            if (hh >= 360.0)
            {
                hh = 0.0f;
            }

            hh = (float)(hh / 60.0);
            var i = (long)MathF.Truncate(hh);
            var ff = hh - i;
            var p = (float)(inv * (1.0 - ins));
            var q = (float)(inv * (1.0 - (ins * ff)));
            var t = (float)(inv * (1.0 - (ins * (1.0 - ff))));
            (outR, outG, outB) = i switch
            {
                0 => (inv, t, p),
                1 => (q, inv, p),
                2 => (p, inv, t),
                3 => (p, q, inv),
                4 => (t, p, inv),
                _ => (inv, p, q),
            };
        }

        return new Rgb8(
            Clamp(Math.Round(outR * 255.0, MidpointRounding.AwayFromZero)),
            Clamp(Math.Round(outG * 255.0, MidpointRounding.AwayFromZero)),
            Clamp(Math.Round(outB * 255.0, MidpointRounding.AwayFromZero)));
    }

    /// <summary><c>ColorRgb::clamp(int)</c> of a value the C++ code converts to int first (truncation).</summary>
    private static byte Clamp(double x)
    {
        var v = (int)x;
        return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}
