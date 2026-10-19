using System.Numerics;

namespace Overglow.Core.Color;

/// <summary>RGB/HSV conversions on 0..1 floats (hue in degrees). Port of ColorSpaceMath::rgb2hsv / hsv2rgb.</summary>
public static class Hsv
{
    /// <summary>std::numeric_limits&lt;float&gt;::epsilon() (not <see cref="float.Epsilon"/>, which is the smallest denormal).</summary>
    private const float FloatEpsilon = 1.1920929E-07f;

    public static Vector3 FromRgb(Vector3 rgb)
    {
        var cmax = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        var cmin = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        var diff = cmax - cmin;

        float h;
        if (cmax == cmin)
        {
            h = 0;
        }
        else if (cmax == rgb.X)
        {
            h = ((60 * ((rgb.Y - rgb.Z) / diff)) + 360) % 360;
        }
        else if (cmax == rgb.Y)
        {
            h = ((60 * ((rgb.Z - rgb.X) / diff)) + 120) % 360;
        }
        else
        {
            h = ((60 * ((rgb.X - rgb.Y) / diff)) + 240) % 360;
        }

        var s = cmax <= FloatEpsilon ? 0 : diff / cmax;
        return new Vector3(h, s, cmax);
    }

    public static Vector3 ToRgb(Vector3 hsv)
    {
        var c = hsv.Z * hsv.Y;
        var x = c * (1 - MathF.Abs((hsv.X / 60 % 2) - 1));
        var m = hsv.Z - c;
        var rgb = (int)(hsv.X / 60) switch
        {
            0 => new Vector3(c, x, 0),
            1 => new Vector3(x, c, 0),
            2 => new Vector3(0, c, x),
            3 => new Vector3(0, x, c),
            4 => new Vector3(x, 0, c),
            _ => new Vector3(c, 0, x),
        };
        return new Vector3(rgb.X + m, rgb.Y + m, rgb.Z + m);
    }
}
