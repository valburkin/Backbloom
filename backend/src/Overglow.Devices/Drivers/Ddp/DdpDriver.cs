using System.Numerics;
using System.Text.Json.Nodes;
using Overglow.Core.Color;
using Overglow.Devices.Rgbw;

namespace Overglow.Devices.Drivers.Ddp;

/// <summary>Settings of the "ddp" driver (sources/led-drivers/schemas/schema-ddp.json).</summary>
/// <param name="Rgbw">RGBW by the Infinite Color Engine (float colors, dithered).</param>
/// <param name="ClassicRgbw">Classic 8-bit RGBW conversion (<c>rgbw</c> + <c>white_algorithm</c>), or null for RGB.</param>
public sealed record DdpOptions(
    string Host,
    int Port = DdpProtocol.DefaultPort,
    ColorOrder ColorOrder = ColorOrder.Rgb,
    InfiniteRgbwOptions? Rgbw = null,
    RgbwConverter? ClassicRgbw = null)
{
    public static DdpOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new DdpOptions(
            DeviceConfig.GetString(config, "host"),
            DeviceConfig.GetInt(config, "port", DdpProtocol.DefaultPort),
            ColorOrders.Parse(DeviceConfig.GetString(config, "colorOrder", "rgb")),
            InfiniteRgbwOptions.FromConfig(config),
            RgbwConverter.FromConfig(config));
    }
}

/// <summary>DDP over UDP (WLED, ESPixelStick, xLights...): RGB, classic RGBW or RGBW by the Infinite Color Engine. Port of <c>DriverNetDDP</c>.</summary>
public sealed class DdpDriver : UdpLedDriver, IFloatColorDriver
{
    public const string TypeName = "ddp";

    private readonly DdpOptions _options;
    private readonly InfiniteRgbwOptions _rgbw;
    private readonly InfiniteRgbwEncoder _encoder = new();
    private readonly byte[] _packet = new byte[DdpProtocol.MaxPacketSize];
    private byte[] _rgbwBuffer = [];
    private byte _sequence;

    public DdpDriver(DdpOptions options, DriverContext context)
        : base(options?.Host ?? string.Empty, options?.Port ?? 0, context, "DDP")
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _rgbw = options.Rgbw ?? InfiniteRgbwOptions.Disabled;
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new DdpDriver(DdpOptions.FromConfig(config), context));

    public override string Type => TypeName;

    public bool WritesFloatColors => _rgbw.Enabled;

    public override bool Write(ReadOnlySpan<byte> colors)
    {
        if (!Connected)
        {
            return false;
        }

        if (_options.ClassicRgbw is { } converter)
        {
            var rgbw = RgbwBuffer(colors.Length / 3);
            converter.Convert(colors, rgbw);
            return Send(rgbw, DdpProtocol.DataTypeRgbw);
        }

        return Send(colors, DdpProtocol.DataTypeRgb);
    }

    public bool WriteFloat(ReadOnlySpan<Vector3> colors, TimeSpan frameInterval)
    {
        if (!Connected)
        {
            return false;
        }

        var rgbw = RgbwBuffer(colors.Length);
        _encoder.Render(colors, (int)frameInterval.TotalMilliseconds, _rgbw, _options.ColorOrder, rgbw);
        return Send(rgbw, DdpProtocol.DataTypeRgbw);
    }

    private byte[] RgbwBuffer(int ledCount)
    {
        if (_rgbwBuffer.Length != ledCount * 4)
        {
            _rgbwBuffer = new byte[ledCount * 4];
        }

        return _rgbwBuffer;
    }

    private bool Send(ReadOnlySpan<byte> data, byte dataType)
    {
        _sequence = DdpProtocol.NextSequence(_sequence);
        var ok = true;
        var packets = DdpProtocol.PacketCountForBytes(data.Length);
        for (var i = 0; i < packets; i++)
        {
            var length = DdpProtocol.WriteFrame(data, i, _sequence, dataType, _packet);
            ok &= Channel.Send(_packet.AsSpan(0, length));
        }

        return ok;
    }
}
