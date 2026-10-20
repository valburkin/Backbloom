using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Overglow.Core.Color;
using Overglow.Devices.Rgbw;

namespace Overglow.Devices.Drivers.ArtNet;

/// <summary>Settings of the "udpartnet" driver (sources/led-drivers/schemas/schema-artnet.json).</summary>
public sealed record ArtNetOptions(
    string Host,
    int Port = ArtNetProtocol.DefaultPort,
    int Universe = 1,
    int ChannelsPerFixture = 3,
    bool DisableSplitting = false,
    ColorOrder ColorOrder = ColorOrder.Rgb,
    InfiniteRgbwOptions? Rgbw = null)
{
    public static ArtNetOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new ArtNetOptions(
            DeviceConfig.GetString(config, "host"),
            DeviceConfig.GetInt(config, "port", ArtNetProtocol.DefaultPort),
            DeviceConfig.GetInt(config, "universe", 1),
            DeviceConfig.GetInt(config, "channelsPerFixture", 3),
            DeviceConfig.GetBool(config, "disableSplitting", false),
            ColorOrders.Parse(DeviceConfig.GetString(config, "colorOrder", "rgb")),
            InfiniteRgbwOptions.FromConfig(config));
    }
}

/// <summary>
/// ArtDmx packets (port of <c>DriverNetUdpArtNet</c>, sources/led-drivers/net/DriverNetUdpArtNet.cpp): "Art-Net",
/// OpOutput 0x5000 and protocol 14 in little endian, sequence, physical 0, 15-bit port address, big-endian data length
/// (even, at most 512), then the channels.
/// </summary>
public static class ArtNetProtocol
{
    public const int DefaultPort = 6454;
    public const int HeaderSize = 18;
    public const int DmxMax = 512;
    public const int MaxPacketSize = HeaderSize + DmxMax;

    /// <summary>The next sequence number: 1..255 (0 would switch resequencing off).</summary>
    public static byte NextSequence(byte previous) => previous == 255 ? (byte)1 : (byte)(previous + 1);

    /// <summary>Writes the header for <paramref name="dataLength"/> channels already in place; returns the packet length.</summary>
    public static int WriteHeader(Span<byte> packet, int universe, byte sequence, int dataLength)
    {
        var length = Math.Min((dataLength & 1) == 1 ? dataLength + 1 : dataLength, DmxMax);
        "Art-Net\0"u8.CopyTo(packet);
        packet[8] = 0x00; // OpCode 0x5000 (OpOutput / OpDmx), little endian
        packet[9] = 0x50;
        packet[10] = 0x00; // protocol version 14, big endian
        packet[11] = 0x0e;
        packet[12] = sequence;
        packet[13] = 0;
        packet[14] = (byte)(universe & 0xff);
        packet[15] = (byte)((universe >> 8) & 0x7f);
        BinaryPrimitives.WriteUInt16BigEndian(packet[16..], (ushort)length);
        return HeaderSize + length;
    }
}

/// <summary>Art-Net over UDP, RGB or RGBW by the Infinite Color Engine.</summary>
public sealed class ArtNetDriver : UdpLedDriver, IFloatColorDriver
{
    public const string TypeName = "udpartnet";

    private readonly ArtNetOptions _options;
    private readonly InfiniteRgbwOptions _rgbw;
    private readonly InfiniteRgbwEncoder _encoder = new();
    private readonly byte[] _packet = new byte[ArtNetProtocol.MaxPacketSize];
    private byte[] _rgbwBuffer = [];
    private byte _sequence = 1;

    public ArtNetDriver(ArtNetOptions options, DriverContext context)
        : base(options?.Host ?? string.Empty, options?.Port ?? 0, context, "Art-Net")
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _rgbw = options.Rgbw ?? InfiniteRgbwOptions.Disabled;
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new ArtNetDriver(ArtNetOptions.FromConfig(config), context));

    public override string Type => TypeName;

    public bool WritesFloatColors => _rgbw.Enabled;

    public override bool Write(ReadOnlySpan<byte> colors)
    {
        if (!Connected)
        {
            return false;
        }

        _sequence = ArtNetProtocol.NextSequence(_sequence);
        var channelsPerFixture = Math.Max(3, _options.ChannelsPerFixture);
        var universe = _options.Universe;
        var ok = true;
        var dmx = 0;
        _packet.AsSpan().Clear();
        for (var i = 0; i < colors.Length; i++)
        {
            _packet[ArtNetProtocol.HeaderSize + dmx++] = colors[i];
            if (i % 3 == 2)
            {
                dmx += channelsPerFixture - 3;
            }

            if (i == colors.Length - 1 || dmx >= ArtNetProtocol.DmxMax || (_options.DisableSplitting && dmx + channelsPerFixture > ArtNetProtocol.DmxMax))
            {
                var length = ArtNetProtocol.WriteHeader(_packet, universe, _sequence, dmx);
                ok &= Channel.Send(_packet.AsSpan(0, length));
                _packet.AsSpan().Clear();
                universe++;
                dmx = 0;
            }
        }

        return ok;
    }

    public bool WriteFloat(ReadOnlySpan<Vector3> colors, TimeSpan frameInterval)
    {
        if (!Connected)
        {
            return false;
        }

        if (_rgbwBuffer.Length != colors.Length * 4)
        {
            _rgbwBuffer = new byte[colors.Length * 4];
        }

        _encoder.Render(colors, (int)frameInterval.TotalMilliseconds, _rgbw, _options.ColorOrder, _rgbwBuffer);

        var channelsPerFixture = Math.Max(4, _options.ChannelsPerFixture);
        var universe = _options.Universe;
        var dmx = 0;
        var ok = true;
        _packet.AsSpan().Clear();
        for (var led = 0; led < colors.Length; led++)
        {
            if (_options.DisableSplitting && dmx + channelsPerFixture > ArtNetProtocol.DmxMax)
            {
                Flush();
            }

            for (var ch = 0; ch < channelsPerFixture; ch++)
            {
                if (dmx >= ArtNetProtocol.DmxMax)
                {
                    Flush();
                }

                _packet[ArtNetProtocol.HeaderSize + dmx++] = ch < 4 ? _rgbwBuffer[(led * 4) + ch] : (byte)0;
            }
        }

        Flush();
        return ok;

        void Flush()
        {
            if (dmx == 0)
            {
                return;
            }

            _sequence = ArtNetProtocol.NextSequence(_sequence);
            var length = ArtNetProtocol.WriteHeader(_packet, universe, _sequence, dmx);
            ok &= Channel.Send(_packet.AsSpan(0, length));
            _packet.AsSpan().Clear();
            universe++;
            dmx = 0;
        }
    }
}
