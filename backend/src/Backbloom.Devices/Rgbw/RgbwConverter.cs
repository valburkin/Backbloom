using System.Text.Json.Nodes;

namespace Backbloom.Devices.Rgbw;

/// <summary>How the white channel of an RGBW strip is derived from RGB (the <c>white_algorithm</c> setting).</summary>
public enum WhiteAlgorithm
{
    Invalid,
    SubtractMinimum,
    SubMinWarmAdjust,
    SubMinCoolAdjust,
    WhiteOff,
    HyperserialColdWhite,
    HyperserialNeutralWhite,
    HyperserialCustom,
    WledAuto,
    WledAutoMax,
    WledAutoAccurate,
}

/// <summary>
/// Classic 8-bit RGB to RGBW conversion. Port of <c>RGBW::stringToWhiteAlgorithm</c>, <c>prepareRgbwCalibration</c>
/// and <c>rgb2rgbw</c> (sources/led-drivers/ColorRgbw.cpp). The HyperSerial algorithms use per-channel lookup tables
/// that are built once here.
/// </summary>
public sealed class RgbwConverter
{
    private readonly byte[] _white = new byte[256];
    private readonly byte[] _red = new byte[256];
    private readonly byte[] _green = new byte[256];
    private readonly byte[] _blue = new byte[256];

    /// <param name="algorithm">Must not be <see cref="WhiteAlgorithm.Invalid"/>.</param>
    /// <param name="gain">White channel limit (HyperSerial custom calibration only).</param>
    /// <param name="red">Red part of the white LED (HyperSerial custom calibration only).</param>
    /// <param name="green">Green part of the white LED (HyperSerial custom calibration only).</param>
    /// <param name="blue">Blue part of the white LED (HyperSerial custom calibration only).</param>
    public RgbwConverter(WhiteAlgorithm algorithm, byte gain = 255, byte red = 255, byte green = 255, byte blue = 255)
    {
        if (algorithm == WhiteAlgorithm.Invalid)
        {
            throw new ArgumentException("Unknown white channel algorithm.", nameof(algorithm));
        }

        Algorithm = algorithm;
        switch (algorithm)
        {
            case WhiteAlgorithm.HyperserialColdWhite:
                (gain, red, green, blue) = (0xFF, 0xA0, 0xA0, 0xA0);
                break;
            case WhiteAlgorithm.HyperserialNeutralWhite:
                (gain, red, green, blue) = (0xFF, 0xB0, 0xB0, 0x70);
                break;
            case WhiteAlgorithm.HyperserialCustom:
                break;
            default:
                return;
        }

        for (var i = 0; i < 256; i++)
        {
            _white[i] = Scale(gain, i);
            _red[i] = Scale(red, i);
            _green[i] = Scale(green, i);
            _blue[i] = Scale(blue, i);
        }

        // ROUND_DIVIDE(factor * i, 0xFF), capped at 0xFF
        static byte Scale(byte factor, int i) => (byte)Math.Min((uint)((factor * i) + 127) / 255, 0xFFu);
    }

    public WhiteAlgorithm Algorithm { get; }

    /// <summary>Parses the <c>white_algorithm</c> setting; an empty value means "white_off".</summary>
    public static WhiteAlgorithm ParseAlgorithm(string? name) => name switch
    {
        "subtract_minimum" => WhiteAlgorithm.SubtractMinimum,
        "sub_min_warm_adjust" => WhiteAlgorithm.SubMinWarmAdjust,
        "sub_min_cool_adjust" => WhiteAlgorithm.SubMinCoolAdjust,
        "hyperserial_cold_white" => WhiteAlgorithm.HyperserialColdWhite,
        "hyperserial_neutral_white" => WhiteAlgorithm.HyperserialNeutralWhite,
        "hyperserial_custom" => WhiteAlgorithm.HyperserialCustom,
        "wled_auto" => WhiteAlgorithm.WledAuto,
        "wled_auto_max" => WhiteAlgorithm.WledAutoMax,
        "wled_auto_accurate" => WhiteAlgorithm.WledAutoAccurate,
        null or "" or "white_off" => WhiteAlgorithm.WhiteOff,
        _ => WhiteAlgorithm.Invalid,
    };

    /// <summary>
    /// The converter for the classic RGBW settings of a driver (<c>rgbw</c>, <c>white_algorithm</c>,
    /// <c>white_channel_limit</c> in percent, <c>white_channel_red/green/blue</c>), or null when RGBW is off.
    /// </summary>
    public static RgbwConverter? FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!DeviceConfig.GetBool(config, "rgbw", false))
        {
            return null;
        }

        var name = DeviceConfig.GetString(config, "white_algorithm", "hyperserial_cold_white");
        var algorithm = ParseAlgorithm(name);
        if (algorithm == WhiteAlgorithm.Invalid)
        {
            throw new ArgumentException($"Unknown white channel algorithm '{name}'.", nameof(config));
        }

        return new RgbwConverter(
            algorithm,
            WhiteChannelLimit(config),
            (byte)Math.Clamp(DeviceConfig.GetInt(config, "white_channel_red", 255), 0, 255),
            (byte)Math.Clamp(DeviceConfig.GetInt(config, "white_channel_green", 255), 0, 255),
            (byte)Math.Clamp(DeviceConfig.GetInt(config, "white_channel_blue", 255), 0, 255));
    }

    /// <summary><c>white_channel_limit</c> (percent) as a 0..255 gain: qMin(qRound(limit * 255 / 100), 255).</summary>
    public static byte WhiteChannelLimit(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var limit = DeviceConfig.GetDouble(config, "white_channel_limit", 1);
        return (byte)Math.Clamp(QRound(limit * 255.0 / 100.0), 0, 255);
    }

    /// <summary>qRound(double): rounds half up for positive values and half down for negative ones.</summary>
    internal static int QRound(double d) => d >= 0.0 ? (int)(d + 0.5) : (int)(d - 0.5);

    /// <summary>Converts three bytes per LED into four (R, G, B, W). <paramref name="rgbw"/> holds at least 4/3 as much.</summary>
    public void Convert(ReadOnlySpan<byte> rgb, Span<byte> rgbw)
    {
        var leds = rgb.Length / 3;
        if (rgbw.Length < leds * 4)
        {
            throw new ArgumentException("The RGBW buffer is too small.", nameof(rgbw));
        }

        for (var i = 0; i < leds; i++)
        {
            Convert(rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2], rgbw.Slice(i * 4, 4));
        }
    }

    private void Convert(byte r, byte g, byte b, Span<byte> o)
    {
        switch (Algorithm)
        {
            case WhiteAlgorithm.SubtractMinimum:
            case WhiteAlgorithm.WledAutoAccurate:
            {
                var w = Math.Min(Math.Min(r, g), b);
                (o[0], o[1], o[2], o[3]) = ((byte)(r - w), (byte)(g - w), (byte)(b - w), w);
                break;
            }

            case WhiteAlgorithm.SubMinCoolAdjust:
            case WhiteAlgorithm.SubMinWarmAdjust:
            {
                // http://forum.garagecube.com/viewtopic.php?t=10178
                var cool = Algorithm == WhiteAlgorithm.SubMinCoolAdjust;
                var f1 = cool ? 0.299f : 0.274f;
                var f2 = cool ? 0.587f : 0.454f;
                var f3 = cool ? 0.114f : 2.333f;
                var w = MathF.Min(MathF.Min(r * f1, g * f2), b * f3);
                o[3] = ClampRound(w);
                o[0] = ClampRound(r - (w / f1));
                o[1] = ClampRound(g - (w / f2));
                o[2] = ClampRound(b - (w / f3));
                break;
            }

            case WhiteAlgorithm.WledAutoMax:
                (o[0], o[1], o[2], o[3]) = (r, g, b, Math.Max(Math.Max(r, g), b));
                break;

            case WhiteAlgorithm.WledAuto:
                (o[0], o[1], o[2], o[3]) = (r, g, b, Math.Min(Math.Min(r, g), b));
                break;

            case WhiteAlgorithm.HyperserialNeutralWhite:
            case WhiteAlgorithm.HyperserialColdWhite:
            case WhiteAlgorithm.HyperserialCustom:
            {
                var white = Math.Min(_red[r], Math.Min(_green[g], _blue[b]));
                o[0] = (byte)(r - _red[white]);
                o[1] = (byte)(g - _green[white]);
                o[2] = (byte)(b - _blue[white]);
                o[3] = _white[white];
                break;
            }

            case WhiteAlgorithm.WhiteOff:
            default:
                (o[0], o[1], o[2], o[3]) = (r, g, b, 0);
                break;
        }
    }

    private static byte ClampRound(float value) => (byte)Math.Clamp(MathF.Round(value, MidpointRounding.AwayFromZero), 0f, 255f);
}
