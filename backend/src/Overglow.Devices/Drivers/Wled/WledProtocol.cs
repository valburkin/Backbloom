namespace Overglow.Devices.Drivers.Wled;

/// <summary>
/// WLED UDP realtime packets as sent by <c>DriverNetWled::writeFiniteColors</c> (sources/led-drivers/net/DriverNetWled.cpp):
/// DRGB (protocol 2) for up to 490 LEDs, otherwise DNRGB (protocol 4) chunks of 489 LEDs with a start index.
/// The second byte is the realtime timeout in seconds; 255 keeps WLED in realtime mode until told otherwise.
/// </summary>
public static class WledProtocol
{
    public const byte Drgb = 2;
    public const byte Dnrgb = 4;
    public const byte NoTimeout = 255;
    public const int MaxDrgbLeds = 490;
    public const int MaxDnrgbLeds = 489;
    public const int DefaultUdpPort = 21324;

    /// <summary>Largest datagram <see cref="WriteFrame"/> produces.</summary>
    public const int MaxPacketSize = 2 + (MaxDrgbLeds * 3);

    /// <summary>Number of datagrams needed for <paramref name="ledCount"/> LEDs.</summary>
    public static int PacketCount(int ledCount) =>
        ledCount <= MaxDrgbLeds ? 1 : (ledCount + MaxDnrgbLeds - 1) / MaxDnrgbLeds;

    /// <summary>
    /// Writes packet <paramref name="index"/> of a frame into <paramref name="packet"/> and returns its length.
    /// </summary>
    /// <param name="rgb">Three bytes per LED.</param>
    public static int WriteFrame(ReadOnlySpan<byte> rgb, int index, Span<byte> packet)
    {
        var ledCount = rgb.Length / 3;
        if (ledCount <= MaxDrgbLeds)
        {
            packet[0] = Drgb;
            packet[1] = NoTimeout;
            rgb.CopyTo(packet[2..]);
            return rgb.Length + 2;
        }

        var start = index * MaxDnrgbLeds;
        var count = Math.Min(MaxDnrgbLeds, ledCount - start);
        packet[0] = Dnrgb;
        packet[1] = NoTimeout;
        packet[2] = (byte)((start >> 8) & 0xff);
        packet[3] = (byte)(start & 0xff);
        rgb.Slice(start * 3, count * 3).CopyTo(packet[4..]);
        return (count * 3) + 4;
    }

    /// <summary>
    /// Body for <c>/json/state</c> (port of <c>DriverNetWled::getOnOffRequest</c>): realtime on with an optional
    /// brightness override, or off. When the original state was saved it is restored on power-off instead.
    /// </summary>
    public static string OnOffRequest(bool on, bool overrideBrightness, int brightness, string? savedState)
    {
        if (!on && savedState is not null)
        {
            return savedState;
        }

        var state = on ? "true" : "false";
        var bri = overrideBrightness && on ? $",\"bri\":{brightness}" : string.Empty;
        return $"{{\"on\":{state},\"live\":{state}{bri}}}";
    }
}
