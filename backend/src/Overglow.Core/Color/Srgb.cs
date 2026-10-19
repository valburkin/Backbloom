using System.Numerics;

namespace Overglow.Core.Color;

/// <summary>
/// sRGB transfer function lookup tables, as in HyperHDR's InfiniteProcessing.h: a 256-entry table from 8-bit sRGB to
/// 16-bit linear light (used by image averaging) and two 512-entry float tables with linear interpolation.
/// </summary>
public static class Srgb
{
    public const int LutSize = 512;

    private static readonly ushort[] s_u8ToLinear16 = BuildU8ToLinear16();
    private static readonly float[] s_toLinear = BuildToLinear();
    private static readonly float[] s_toNonlinear = BuildToNonlinear();

    /// <summary>8-bit sRGB value to linear light in 0..65535 (rounded).</summary>
    public static ReadOnlySpan<ushort> U8ToLinear16 => s_u8ToLinear16;

    public static Vector3 ToLinear(Vector3 color) =>
        new(Interpolate(s_toLinear, color.X), Interpolate(s_toLinear, color.Y), Interpolate(s_toLinear, color.Z));

    public static Vector3 ToNonlinear(Vector3 color) =>
        new(Interpolate(s_toNonlinear, color.X), Interpolate(s_toNonlinear, color.Y), Interpolate(s_toNonlinear, color.Z));

    /// <summary>Looks up <paramref name="value"/> (clamped to 0..1) in a table, interpolating linearly between entries.</summary>
    internal static float Interpolate(ReadOnlySpan<float> lut, float value)
    {
        var v = MathF.Max(0.0f, MathF.Min(value, 1.0f));
        var pos = v * (lut.Length - 1);
        var index0 = (int)pos;
        var index1 = Math.Min(index0 + 1, lut.Length - 1);
        var fraction = pos - index0;
        return Lerp(lut[index0], lut[index1], fraction);
    }

    /// <summary>linalg::lerp: <c>a * (1 - t) + b * t</c> (kept in this form to match the C++ results).</summary>
    internal static float Lerp(float a, float b, float t) => (a * (1 - t)) + (b * t);

    private static ushort[] BuildU8ToLinear16()
    {
        var lut = new ushort[256];
        for (var i = 0; i < lut.Length; i++)
        {
            var srgb = i / 255.0f;
            var linear = srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
            lut[i] = (ushort)((linear * 65535.0f) + 0.5f);
        }

        return lut;
    }

    private static float[] BuildToLinear()
    {
        var lut = new float[LutSize];
        for (var i = 0; i < lut.Length; i++)
        {
            var v = (float)i / (LutSize - 1);
            lut[i] = v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }

        return lut;
    }

    private static float[] BuildToNonlinear()
    {
        var lut = new float[LutSize];
        for (var i = 0; i < lut.Length; i++)
        {
            var v = (float)i / (LutSize - 1);
            lut[i] = v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1.0f / 2.4f)) - 0.055f;
        }

        return lut;
    }
}
