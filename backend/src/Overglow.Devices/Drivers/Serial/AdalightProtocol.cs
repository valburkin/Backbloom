using System.Buffers.Binary;

namespace Overglow.Devices.Drivers.Serial;

/// <summary>
/// Adalight frames as built by <c>DriverSerialAdalight</c> (sources/led-drivers/serial/DriverSerialAdalight.cpp):
/// <list type="bullet">
/// <item>classic: "Ada", big-endian LED count - 1, checksum (hi ^ lo ^ 0x55), RGB data;</item>
/// <item>AWA (HyperSerial): "Awa" ("AwA" with the white channel calibration, "AWa" for RGBW by the Infinite Color
/// Engine), the same count and checksum, RGB (or RGBW) data, the optional calibration (limit, red, green, blue) and a
/// Fletcher checksum (f1, f2, extended - 0x41 is replaced by 0xaa);</item>
/// <item>LightBerry APA102: "Ada" with the LED count, a 4-byte start frame, 0xFF + RGB per LED, an end frame of
/// max((n + 15) / 16, 4) zero bytes.</item>
/// </list>
/// </summary>
public static class AdalightProtocol
{
    public const int HeaderSize = 6;

    /// <summary>Writes the 6-byte header.</summary>
    public static void WriteHeader(Span<byte> frame, byte second, byte third, int count)
    {
        frame[0] = (byte)'A';
        frame[1] = second;
        frame[2] = third;
        BinaryPrimitives.WriteUInt16BigEndian(frame[3..], (ushort)count);
        frame[5] = (byte)(frame[3] ^ frame[4] ^ 0x55);
    }

    /// <summary>The header letters for a mode.</summary>
    public static (byte Second, byte Third) Letters(bool awa, bool infiniteRgbw, bool whiteCalibration) =>
        ((byte)(awa ? (infiniteRgbw ? 'W' : 'w') : 'd'), (byte)(awa && whiteCalibration && !infiniteRgbw ? 'A' : 'a'));

    /// <summary>Appends the AWA Fletcher checksum of <paramref name="data"/> at <paramref name="destination"/> (3 bytes).</summary>
    public static void WriteFletcher(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int f1 = 0, f2 = 0, ext = 0;
        byte position = 0;
        foreach (var b in data)
        {
            ext = (ext + (b ^ position++)) % 255;
            f1 = (f1 + b) % 255;
            f2 = (f2 + f1) % 255;
        }

        destination[0] = (byte)f1;
        destination[1] = (byte)f2;
        destination[2] = (byte)(ext != 0x41 ? ext : 0xaa);
    }

    /// <summary>Size of a LightBerry APA102 frame for <paramref name="leds"/> LEDs.</summary>
    public static int Apa102FrameSize(int leds) => HeaderSize + (leds * 4) + 4 + Math.Max((leds + 15) / 16, 4);
}
