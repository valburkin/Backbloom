using System.Numerics;
using System.Text.Json.Nodes;
using Overglow.Core.Color;

namespace Overglow.Devices.Rgbw;

/// <summary>
/// Settings of the "Infinite Color Engine" RGBW mode (<c>enable_ice_rgbw</c>, <c>ice_white_mixer_threshold</c>,
/// <c>ice_white_led_intensity</c>, <c>ice_white_temperatur_red/green/blue</c>), shared by DDP, Art-Net, Adalight and
/// the file driver.
/// </summary>
public sealed record InfiniteRgbwOptions(bool Enabled, float WhiteMixerThreshold, float WhiteLedIntensity, Vector3 WhitePoint)
{
    public static InfiniteRgbwOptions Disabled { get; } = new(false, 0.0f, 1.8f, Vector3.One);

    public static InfiniteRgbwOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new InfiniteRgbwOptions(
            DeviceConfig.GetBool(config, "enable_ice_rgbw", false),
            (float)DeviceConfig.GetDouble(config, "ice_white_mixer_threshold", 0.0),
            (float)DeviceConfig.GetDouble(config, "ice_white_led_intensity", 1.8),
            new Vector3(
                (float)DeviceConfig.GetDouble(config, "ice_white_temperatur_red", 1.0),
                (float)DeviceConfig.GetDouble(config, "ice_white_temperatur_green", 1.0),
                (float)DeviceConfig.GetDouble(config, "ice_white_temperatur_blue", 1.0)));
    }
}

/// <summary>
/// Stateful float RGB to RGBW encoder with temporal dithering: the rounding error of each LED is carried into its next
/// frame while the color keeps moving in the same direction. Port of <c>InfiniteColorEngineRgbw</c>
/// (sources/led-drivers/InfiniteColorEngineRgbw.cpp); the float operations are the same and in the same order, so the
/// output is bit-identical. It applies the controller's color order itself (to the RGB part, white stays last).
/// </summary>
/// <remarks>Allocates only when the LED count changes.</remarks>
public sealed class InfiniteRgbwEncoder
{
    private const float Denominator = 0.00001f;
    private const double MotionThreshold = 0.01 / 255.0;

    private LedState[] _states = [];

    /// <summary>Encodes one frame: four bytes per LED into <paramref name="output"/>.</summary>
    /// <param name="colors">Processed colors, 0..1, RGB order.</param>
    /// <param name="intervalMs">The device's refresh interval (below 17 ms the motion threshold shrinks).</param>
    public void Render(ReadOnlySpan<Vector3> colors, int intervalMs, InfiniteRgbwOptions options, ColorOrder order, Span<byte> output)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (output.Length < colors.Length * 4)
        {
            throw new ArgumentException("The output buffer is too small.", nameof(output));
        }

        if (_states.Length != colors.Length)
        {
            _states = new LedState[colors.Length];
            _states.AsSpan().Fill(LedState.Initial);
        }

        var whiteMixerThreshold255 = options.WhiteMixerThreshold * 255.0f;
        var motionThreshold = intervalMs is > 0 and < 17 ? (float)(MotionThreshold * (intervalMs / 17.0)) : (float)MotionThreshold;
        var whitePoint = options.WhitePoint;
        var customWhite = MinElement(whitePoint) < 0.99999f || MaxElement(whitePoint) > 1.00001f;
        var invWhiteMixer = 255.0f - whiteMixerThreshold255;
        invWhiteMixer = invWhiteMixer > Denominator ? 1.0f / invWhiteMixer : 0.0f;

        for (var i = 0; i < colors.Length; i++)
        {
            Encode(colors[i], ref _states[i], motionThreshold, invWhiteMixer, whiteMixerThreshold255, options.WhiteLedIntensity, whitePoint, customWhite, order);
            var sent = _states[i].LastSent;
            output[i * 4] = sent.A;
            output[(i * 4) + 1] = sent.B;
            output[(i * 4) + 2] = sent.C;
            output[(i * 4) + 3] = sent.W;
        }
    }

    private static void Encode(
        Vector3 rgb,
        ref LedState state,
        float motionThreshold,
        float invWhiteMixer,
        float whiteMixerThreshold255,
        float whiteLedIntensity,
        Vector3 whitePoint,
        bool customWhite,
        ColorOrder order)
    {
        if (state.LastInput == rgb)
        {
            state.Error = Vector3.Zero;
            return;
        }

        if (MaxElement(rgb) < 0.5f / 255.0f)
        {
            state = LedState.Initial;
            state.LastInput = rgb;
            return;
        }

        var diff = rgb - state.LastInput;
        var diffAbs = Vector3.Abs(diff);
        var maxIndex = ArgMax(diffAbs);
        var maxDiff = diff[maxIndex];
        var newFlow = (sbyte)((maxDiff > 0 ? 1 : 0) - (maxDiff < 0 ? 1 : 0));
        var previousFlow = state.Flow;
        state.Flow = newFlow;
        var dithering = previousFlow == newFlow && diffAbs[maxIndex] >= motionThreshold;

        var input255 = dithering ? (rgb * 255.0f) + state.Error : rgb * 255.0f;
        Vector4 target;
        if (whiteLedIntensity > Denominator)
        {
            var common = customWhite ? MinElement(input255 * whitePoint) : MinElement(input255);
            var whiteFactor = invWhiteMixer > Denominator ? Math.Clamp((common - whiteMixerThreshold255) * invWhiteMixer, 0.0f, 1.0f) : 1.0f;
            var baseWhite = common * whiteFactor;
            var white = Round(baseWhite / whiteLedIntensity);
            var subtract = customWhite ? whitePoint * white * whiteLedIntensity : new Vector3(white * whiteLedIntensity);
            var targetRgb = white != 0.0f ? input255 - subtract : input255;
            target = new Vector4(targetRgb, white);
        }
        else
        {
            target = new Vector4(input255, 0.0f);
        }

        var rounded = new Vector4(Clamp255(Round(target.X)), Clamp255(Round(target.Y)), Clamp255(Round(target.Z)), Clamp255(Round(target.W)));

        if (dithering)
        {
            var motion = (diff.X * diff.X) + (diff.Y * diff.Y) + (diff.Z * diff.Z);
            var t = Math.Clamp(motion * 50.0f, 0.0f, 1.0f);
            var leak = (0.96f * (1 - t)) + (0.80f * t);
            state.Error = new Vector3((target.X - rounded.X) * leak, (target.Y - rounded.Y) * leak, (target.Z - rounded.Z) * leak);
        }
        else
        {
            state.Error = Vector3.Zero;
        }

        var (x, y, z, w) = ((byte)rounded.X, (byte)rounded.Y, (byte)rounded.Z, (byte)rounded.W);
        state.LastSent = order switch
        {
            ColorOrder.Bgr => new Bytes4(z, y, x, w),
            ColorOrder.Rbg => new Bytes4(x, z, y, w),
            ColorOrder.Grb => new Bytes4(y, x, z, w),
            ColorOrder.Gbr => new Bytes4(y, z, x, w),
            ColorOrder.Brg => new Bytes4(z, x, y, w),
            _ => new Bytes4(x, y, z, w),
        };
        state.LastInput = rgb;
    }

    private static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    private static float Clamp255(float value) => MathF.Min(MathF.Max(value, 0.0f), 255.0f);

    private static float MinElement(Vector3 v) => MathF.Min(MathF.Min(v.X, v.Y), v.Z);

    private static float MaxElement(Vector3 v) => MathF.Max(MathF.Max(v.X, v.Y), v.Z);

    /// <summary>linalg::argmax: the first index of the largest element.</summary>
    private static int ArgMax(Vector3 v)
    {
        var j = 0;
        if (v.Y > v[j])
        {
            j = 1;
        }

        if (v.Z > v[j])
        {
            j = 2;
        }

        return j;
    }

    private readonly record struct Bytes4(byte A, byte B, byte C, byte W);

    private struct LedState
    {
        public static readonly LedState Initial = new() { LastInput = new Vector3(-1.0f) };

        public Vector3 Error;
        public Vector3 LastInput;
        public Bytes4 LastSent;
        public sbyte Flow;
    }
}
