using System.Numerics;
using System.Text.Json.Nodes;
using Backbloom.Core.Color;
using Backbloom.Devices.Rgbw;

namespace Backbloom.Devices.Drivers.Serial;

/// <summary>Settings of the "adalight" driver (sources/led-drivers/schemas/schema-adalight.json).</summary>
/// <param name="WhiteCalibration">Sends the white channel calibration (AWA only, not with RGBW by the Infinite Color Engine).</param>
public sealed record AdalightOptions(
    SerialOptions Serial,
    bool AwaMode = false,
    bool LightberryApa102 = false,
    bool WhiteCalibration = false,
    byte WhiteLimit = 255,
    byte WhiteRed = 255,
    byte WhiteGreen = 255,
    byte WhiteBlue = 255,
    InfiniteRgbwOptions? Rgbw = null,
    ColorOrder ColorOrder = ColorOrder.Rgb)
{
    public static AdalightOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new AdalightOptions(
            SerialOptions.FromConfig(config, 2000000),
            DeviceConfig.GetBool(config, "awa_mode", false),
            DeviceConfig.GetBool(config, "lightberry_apa102_mode", false),
            DeviceConfig.GetBool(config, "white_channel_calibration", false),
            RgbwConverter.WhiteChannelLimit(config),
            Channel(config, "white_channel_red"),
            Channel(config, "white_channel_green"),
            Channel(config, "white_channel_blue"),
            InfiniteRgbwOptions.FromConfig(config),
            ColorOrders.Parse(DeviceConfig.GetString(config, "colorOrder", "rgb")));

        static byte Channel(JsonObject c, string name) => (byte)Math.Clamp(DeviceConfig.GetInt(c, name, 255), 0, 255);
    }
}

/// <summary>Adalight / HyperSerial (AWA protocol) over a serial port. Port of <c>DriverSerialAdalight</c>.</summary>
public sealed class AdalightDriver : SerialLedDriver, IFloatColorDriver
{
    public const string TypeName = "adalight";

    private readonly AdalightOptions _options;
    private readonly InfiniteRgbwOptions _rgbw;
    private readonly InfiniteRgbwEncoder _encoder = new();
    private byte[] _frame = [];
    private Vector3[] _blackFloats = [];
    private int _leds = -1;
    private bool _floatFrame;

    public AdalightDriver(AdalightOptions options, DriverContext context)
        : base(options?.Serial ?? new SerialOptions(), context)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _rgbw = options.Rgbw ?? InfiniteRgbwOptions.Disabled;
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_3_serial",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new AdalightDriver(AdalightOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
    };

    public override string Type => TypeName;

    public bool WritesFloatColors => _rgbw.Enabled;

    private bool Apa102 => _options.LightberryApa102 && !_options.AwaMode;

    public override bool Write(ReadOnlySpan<byte> colors)
    {
        var leds = colors.Length / 3;
        Prepare(leds, floatFrame: false);
        if (Apa102)
        {
            for (var i = 1; i <= leds; i++)
            {
                _frame[(i * 4) + 7] = colors[((i - 1) * 3) + 0];
                _frame[(i * 4) + 8] = colors[((i - 1) * 3) + 1];
                _frame[(i * 4) + 9] = colors[((i - 1) * 3) + 2];
            }

            return WriteFrame(_frame, leds);
        }

        colors.CopyTo(_frame.AsSpan(AdalightProtocol.HeaderSize));
        var end = AdalightProtocol.HeaderSize + colors.Length;
        if (_options.AwaMode)
        {
            if (_options.WhiteCalibration)
            {
                _frame[end++] = _options.WhiteLimit;
                _frame[end++] = _options.WhiteRed;
                _frame[end++] = _options.WhiteGreen;
                _frame[end++] = _options.WhiteBlue;
            }

            AdalightProtocol.WriteFletcher(_frame.AsSpan(AdalightProtocol.HeaderSize, end - AdalightProtocol.HeaderSize), _frame.AsSpan(end));
            end += 3;
        }

        return WriteFrame(_frame.AsSpan(0, end), leds);
    }

    public bool WriteFloat(ReadOnlySpan<Vector3> colors, TimeSpan frameInterval)
    {
        Prepare(colors.Length, floatFrame: true);
        var data = _frame.AsSpan(AdalightProtocol.HeaderSize, colors.Length * 4);
        _encoder.Render(colors, (int)frameInterval.TotalMilliseconds, _rgbw, _options.ColorOrder, data);
        var end = AdalightProtocol.HeaderSize + data.Length;
        AdalightProtocol.WriteFletcher(data, _frame.AsSpan(end));
        return WriteFrame(_frame.AsSpan(0, end + 3), colors.Length);
    }

    protected override void WriteBlack(ReadOnlySpan<byte> black)
    {
        if (!_rgbw.Enabled)
        {
            Write(black);
            return;
        }

        if (_blackFloats.Length != black.Length / 3)
        {
            _blackFloats = new Vector3[black.Length / 3];
        }

        WriteFloat(_blackFloats, TimeSpan.Zero);
    }

    /// <summary>Rebuilds the header and buffer when the LED count changes (port of <c>CreateHeader</c>).</summary>
    private void Prepare(int leds, bool floatFrame)
    {
        if (leds == _leds && floatFrame == _floatFrame)
        {
            return;
        }

        _leds = leds;
        _floatFrame = floatFrame;
        if (Apa102 && !floatFrame)
        {
            _frame = new byte[AdalightProtocol.Apa102FrameSize(leds)];
            for (var i = 1; i <= leds; i++)
            {
                _frame[(i * 4) + AdalightProtocol.HeaderSize] = 0xFF;
            }

            AdalightProtocol.WriteHeader(_frame, (byte)'d', (byte)'a', leds);
            return;
        }

        _frame = new byte[AdalightProtocol.HeaderSize + (leds * (floatFrame ? 4 : 3)) + 8];
        var (second, third) = AdalightProtocol.Letters(_options.AwaMode, _rgbw.Enabled, _options.WhiteCalibration);
        AdalightProtocol.WriteHeader(_frame, second, third, leds - 1);
    }
}

/// <summary>Skydimo: "Ada", 0, 0, LED count (at most 255), RGB data. Port of <c>DriverSerialSkydimo</c>.</summary>
public sealed class SkydimoDriver(SerialOptions options, DriverContext context) : SerialLedDriver(options, context)
{
    public const string TypeName = "skydimo";

    private byte[] _frame = [];

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_3_serial",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new SkydimoDriver(SerialOptions.FromConfig(config, 115200), context))
    {
        Discover = DiscoverAsync,
    };

    public override string Type => TypeName;

    public override bool Write(ReadOnlySpan<byte> colors)
    {
        if (_frame.Length != AdalightProtocol.HeaderSize + colors.Length)
        {
            _frame = new byte[AdalightProtocol.HeaderSize + colors.Length];
            "Ada"u8.CopyTo(_frame);
            _frame[5] = (byte)Math.Min(colors.Length / 3, 255);
        }

        colors.CopyTo(_frame.AsSpan(AdalightProtocol.HeaderSize));
        return WriteFrame(_frame, colors.Length / 3);
    }
}

/// <summary>SP616E: RGB data followed by a 0xFF terminator (no header). Port of <c>DriverSerialSP616E</c>.</summary>
public sealed class Sp616eDriver(SerialOptions options, DriverContext context) : SerialLedDriver(options, context)
{
    public const string TypeName = "sp616e";
    public const byte Terminator = 0xFF;

    private byte[] _frame = [];

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_3_serial",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new Sp616eDriver(SerialOptions.FromConfig(config, 115200), context))
    {
        Discover = DiscoverAsync,
    };

    public override string Type => TypeName;

    public override bool Write(ReadOnlySpan<byte> colors)
    {
        if (_frame.Length != colors.Length + 1)
        {
            _frame = new byte[colors.Length + 1];
            _frame[^1] = Terminator;
        }

        colors.CopyTo(_frame);
        return WriteFrame(_frame, colors.Length / 3);
    }
}
