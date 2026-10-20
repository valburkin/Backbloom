using System.Globalization;
using System.Numerics;
using System.Text;
using Overglow.Core.Color;

namespace Overglow.Devices.Drivers.PhilipsHue;

/// <summary>
/// Hue Entertainment stream messages and color conversion, as in <c>DriverNetPhilipsHue::prepareStreamData</c>,
/// <c>prepareStreamDataV2</c> and <c>nonLinearRgbToColorXYB</c> (sources/led-drivers/net/DriverNetPhilipsHue.cpp).
/// </summary>
public static class HueProtocol
{
    public const int StreamPort = 2100;
    public const int HeaderSize = 16;

    /// <summary>
    /// Nonlinear RGB to CIE xy + brightness (Wide RGB D65 primaries): XYZ = M * rgb (clamped at 0), x = X / sum,
    /// y = Y / sum, brightness = max(r, g, b); black below 1e-5.
    /// </summary>
    public static Vector3 RgbToXyb(Vector3 nonlinear, bool convertToLinear)
    {
        var c = convertToLinear ? Srgb.ToLinear(nonlinear) : nonlinear;
        var x = MathF.Max((0.664511f * c.X) + (0.154324f * c.Y) + (0.162028f * c.Z), 0.0f);
        var y = MathF.Max((0.283881f * c.X) + (0.668433f * c.Y) + (0.047685f * c.Z), 0.0f);
        var z = MathF.Max((0.000088f * c.X) + (0.072310f * c.Y) + (0.986039f * c.Z), 0.0f);
        var bri = MathF.Max(MathF.Max(c.X, c.Y), c.Z);
        if (bri < 1e-5f)
        {
            return Vector3.Zero;
        }

        var sum = x + y + z;
        return sum < 1e-10f ? Vector3.Zero : new Vector3(x / sum, y / sum, bri);
    }

    /// <summary>Size of a v1 message for <paramref name="lights"/> lights: header + 9 bytes per light.</summary>
    public static int V1Size(int lights) => HeaderSize + (9 * lights);

    /// <summary>Size of a v2 message: header, the 36-character entertainment configuration id, 7 bytes per channel.</summary>
    public static int V2Size(int channels, string configurationId) => HeaderSize + Encoding.Latin1.GetByteCount(configurationId) + (7 * channels);

    /// <summary>"HueStream" v1.0, sequence, xy + brightness color space, then per light: type 0, id (16-bit), x, y, brightness.</summary>
    public static int WriteV1(Span<byte> packet, byte sequence, ReadOnlySpan<int> ids, ReadOnlySpan<Vector3> colors)
    {
        WriteHeader(packet, 0x01, sequence);
        var o = HeaderSize;
        for (var i = 0; i < ids.Length; i++)
        {
            packet[o++] = 0;
            packet[o++] = 0;
            packet[o++] = (byte)ids[i];
            o = WriteXyb(packet, o, colors[i]);
        }

        return o;
    }

    /// <summary>"HueStream" v2.0, sequence, color space, configuration id, then per channel: id (8-bit), x, y, brightness.</summary>
    public static int WriteV2(Span<byte> packet, byte sequence, string configurationId, ReadOnlySpan<int> channels, ReadOnlySpan<Vector3> colors)
    {
        ArgumentNullException.ThrowIfNull(configurationId);
        WriteHeader(packet, 0x02, sequence);
        var o = HeaderSize + Encoding.Latin1.GetBytes(configurationId, packet[HeaderSize..]);
        for (var i = 0; i < channels.Length; i++)
        {
            packet[o++] = (byte)(channels[i] & 0xff);
            o = WriteXyb(packet, o, colors[i]);
        }

        return o;
    }

    /// <summary>A light state command with xy (four significant digits, like QString::arg(v, 0, 'd', 4)) and brightness.</summary>
    public static string XyBri(Vector3 color, int bri) =>
        string.Create(CultureInfo.InvariantCulture, $"\"xy\":[{Digits(color.X)},{Digits(color.Y)}],\"bri\":{bri}");

    /// <summary>Qt's 'g'-style formatting with 4 significant digits (an unknown format letter means 'g' in QString::arg).</summary>
    public static string Digits(float value) => ((double)value).ToString("G4", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);

    /// <summary>The REST brightness: round(min(254, factor * max(1, z * 254))).</summary>
    public static int RestBrightness(float z, float factor) =>
        (int)MathF.Round(MathF.Min(254.0f, factor * MathF.Max(1.0f, z * 254.0f)), MidpointRounding.AwayFromZero);

    private static void WriteHeader(Span<byte> packet, byte major, byte sequence)
    {
        "HueStream"u8.CopyTo(packet);
        packet[9] = major;
        packet[10] = 0x00;
        packet[11] = sequence;
        packet[12] = 0x00;
        packet[13] = 0x00;
        packet[14] = 0x01; // COLORSPACE_XYBRI
        packet[15] = 0x00;
    }

    private static int WriteXyb(Span<byte> packet, int o, Vector3 xyb)
    {
        for (var c = 0; c < 3; c++)
        {
            var value = (ushort)(xyb[c] * 0xffff);
            packet[o++] = (byte)((value >> 8) & 0xff);
            packet[o++] = (byte)(value & 0xff);
        }

        return o;
    }
}
