using System.Numerics;

namespace Backbloom.Core.Color;

/// <summary>Converts processed 0..1 colors to 8-bit LED values in a controller's byte order.</summary>
public static class ColorQuantizer
{
    /// <summary>
    /// Rounds <c>value * 255</c> half away from zero after clamping to 0..255, like
    /// <c>ColorSpaceMath::round_to_0_255</c> (std::lround).
    /// </summary>
    public static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value * 255.0f, 0.0f, 255.0f), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Writes three bytes per color into <paramref name="destination"/> in the given order (e.g. GRB writes green,
    /// red, blue). The destination must hold at least <c>colors.Length * 3</c> bytes.
    /// </summary>
    public static void ToBytes(ReadOnlySpan<Vector3> colors, ColorOrder order, Span<byte> destination)
    {
        if (destination.Length < colors.Length * 3)
        {
            throw new ArgumentException("The destination is too small for the colors.", nameof(destination));
        }

        for (var i = 0; i < colors.Length; i++)
        {
            var r = ToByte(colors[i].X);
            var g = ToByte(colors[i].Y);
            var b = ToByte(colors[i].Z);
            var o = i * 3;
            switch (order)
            {
                case ColorOrder.Bgr:
                    (destination[o], destination[o + 1], destination[o + 2]) = (b, g, r);
                    break;
                case ColorOrder.Rbg:
                    (destination[o], destination[o + 1], destination[o + 2]) = (r, b, g);
                    break;
                case ColorOrder.Grb:
                    (destination[o], destination[o + 1], destination[o + 2]) = (g, r, b);
                    break;
                case ColorOrder.Gbr:
                    (destination[o], destination[o + 1], destination[o + 2]) = (g, b, r);
                    break;
                case ColorOrder.Brg:
                    (destination[o], destination[o + 1], destination[o + 2]) = (b, r, g);
                    break;
                case ColorOrder.Rgb:
                default:
                    (destination[o], destination[o + 1], destination[o + 2]) = (r, g, b);
                    break;
            }
        }
    }
}
