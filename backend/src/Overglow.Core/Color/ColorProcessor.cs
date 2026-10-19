using System.Numerics;

namespace Overglow.Core.Color;

/// <summary>
/// Turns averaged linear-light LED colors into final nonlinear (sRGB-encoded) 0..1 values: temperature, calibration,
/// output scale, sRGB encoding, user gamma, luminance/saturation gain, minimal backlight and power limit.
/// Port of HyperHDR's <c>InfiniteProcessing</c> (sources/infinite-color-engine/InfiniteProcessing.cpp), minus the
/// color order swap, which belongs to each LED device here because one pipeline feeds several controllers.
/// </summary>
/// <remarks>
/// Instances are immutable and every lookup table is built in the constructor, so <see cref="Process"/> does not
/// allocate and a new processor can be swapped in atomically when the settings change.
/// </remarks>
public sealed class ColorProcessor
{
    public const int CalibrationLutDimension = 9;

    private const float GainEpsilon = 1e-5f;
    private const float DefaultMinimalBacklight = 0.0039f;

    private readonly bool _enabled;

    private readonly bool _hasTemperature;
    private readonly Vector3 _temperature;

    private readonly CalibrationMode _calibration;
    private readonly Vector3 _rowRed;
    private readonly Vector3 _rowGreen;
    private readonly Vector3 _rowBlue;
    private readonly Vector3[] _calibrationLut = [];

    private readonly bool _hasScaleOutput;
    private readonly float _scaleOutput;

    private readonly float[]? _gammaLut;

    private readonly bool _hasGain;
    private readonly float _luminanceGain;
    private readonly float _saturationGain;

    private readonly bool _hasBacklight;
    private readonly float _minimalBacklight;
    private readonly bool _coloredBacklight;

    private readonly bool _hasPowerLimit;
    private readonly float _powerLimit;

    public ColorProcessor(ColorAdjustment adjustment, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        Adjustment = adjustment;
        _enabled = enabled;

        // setTemperature
        (_hasTemperature, _temperature) = adjustment.Temperature switch
        {
            TemperaturePreset.Warm => (true, new Vector3(1.0f, 0.93f, 0.85f)),
            TemperaturePreset.Neutral => (true, new Vector3(1.0f, 1.0f, 1.0f)),
            TemperaturePreset.Cold => (true, new Vector3(0.9f, 0.95f, 1.0f)),
            TemperaturePreset.Custom => (true, adjustment.CustomTemperature),
            _ => (false, Vector3.One),
        };

        // generateColorspace
        if (adjustment.ClassicConfig)
        {
            var identity = adjustment.Red == new Rgb8(255, 0, 0) && adjustment.Green == new Rgb8(0, 255, 0) &&
                adjustment.Blue == new Rgb8(0, 0, 255);
            if (!identity)
            {
                // The C++ code builds a matrix whose columns are the targets and then transposes it, so each target
                // color becomes a row: out.R = dot(red target, color), and so on. Pure red therefore becomes
                // (red.R, green.R, blue.R), unlike the LUT mode below, which maps it to the red target
                // (fixtures/color/cpp-calibrate-matrix*.json); kept as in C++ until checked on the hardware.
                _calibration = CalibrationMode.Matrix;
                _rowRed = adjustment.Red.ToVector();
                _rowGreen = adjustment.Green.ToVector();
                _rowBlue = adjustment.Blue.ToVector();
            }
        }
        else if (!IsIdentityCube(adjustment))
        {
            _calibration = CalibrationMode.Lut;
            _calibrationLut = BuildCalibrationLut(adjustment);
        }

        // setScaleOutput
        _scaleOutput = Math.Clamp(adjustment.ScaleOutput, 0.0f, 2.0f);
        _hasScaleOutput = _scaleOutput != 1.0f;

        // generateUserGamma
        if (adjustment.Gamma != 1.0f)
        {
            _gammaLut = new float[Srgb.LutSize];
            for (var i = 0; i < _gammaLut.Length; i++)
            {
                _gammaLut[i] = MathF.Pow((float)i / (Srgb.LutSize - 1), adjustment.Gamma);
            }
        }

        // setBrightnessAndSaturation
        if (MathF.Abs(adjustment.LuminanceGain - 1.0f) >= GainEpsilon || MathF.Abs(adjustment.SaturationGain - 1.0f) >= GainEpsilon)
        {
            _hasGain = true;
            _luminanceGain = MathF.Max(0.0f, adjustment.LuminanceGain);
            _saturationGain = MathF.Max(0.0f, adjustment.SaturationGain);
        }

        // setMinimalBacklight
        var backlight = adjustment.BacklightThreshold >= 1.0f ? DefaultMinimalBacklight : adjustment.BacklightThreshold;
        if (backlight != 0.0f)
        {
            _hasBacklight = true;
            _minimalBacklight = backlight;
            _coloredBacklight = adjustment.BacklightColored;
        }

        // setPowerLimit
        _powerLimit = Math.Clamp(adjustment.PowerLimit, 0.0f, 1.0f);
        _hasPowerLimit = _powerLimit != 1.0f;
    }

    private enum CalibrationMode
    {
        None,
        Matrix,
        Lut,
    }

    public static ColorProcessor Identity { get; } = new(ColorAdjustment.Default with { Gamma = 1.0f });

    /// <summary>The adjustment this processor was built from.</summary>
    public ColorAdjustment Adjustment { get; }

    /// <summary>
    /// Processes colors in place: input is linear light 0..1, output is nonlinear 0..1 ready to be quantized.
    /// Port of <c>InfiniteProcessing::applyyAllProcessingSteps</c>.
    /// </summary>
    public void Process(Span<Vector3> colors)
    {
        if (!_enabled)
        {
            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] = Srgb.ToNonlinear(colors[i]);
            }

            return;
        }

        for (var i = 0; i < colors.Length; i++)
        {
            var color = colors[i];
            ApplyTemperature(ref color);
            Calibrate(ref color);
            ApplyScaleOutput(ref color);
            color = Srgb.ToNonlinear(color);
            ApplyUserGamma(ref color);
            ApplyLuminanceAndSaturation(ref color);
            ApplyMinimalBacklight(ref color);
            colors[i] = color;
        }

        ApplyPowerLimit(colors);
    }

    internal void ApplyTemperature(ref Vector3 color)
    {
        if (_hasTemperature)
        {
            color = Vector3.Clamp(color * _temperature, Vector3.Zero, Vector3.One);
        }
    }

    internal void Calibrate(ref Vector3 color)
    {
        switch (_calibration)
        {
            case CalibrationMode.Matrix:
                color = Vector3.Clamp(new Vector3(Dot(_rowRed, color), Dot(_rowGreen, color), Dot(_rowBlue, color)), Vector3.Zero, Vector3.One);
                break;
            case CalibrationMode.Lut:
                color = Vector3.Clamp(SampleCalibrationLut(color), Vector3.Zero, Vector3.One);
                break;
            case CalibrationMode.None:
            default:
                break;
        }
    }

    internal void ApplyScaleOutput(ref Vector3 color)
    {
        if (_hasScaleOutput)
        {
            color = Vector3.Clamp(color * _scaleOutput, Vector3.Zero, Vector3.One);
        }
    }

    internal void ApplyUserGamma(ref Vector3 color)
    {
        if (_gammaLut is null)
        {
            return;
        }

        color = new Vector3(Gamma(_gammaLut, color.X), Gamma(_gammaLut, color.Y), Gamma(_gammaLut, color.Z));

        static float Gamma(float[] lut, float value)
        {
            var pos = value * (Srgb.LutSize - 1);
            var index0 = Math.Min((int)pos, Srgb.LutSize - 1);
            var index1 = Math.Min(index0 + 1, Srgb.LutSize - 1);
            var fraction = pos - index0;
            return Srgb.Lerp(lut[index0], lut[index1], fraction);
        }
    }

    internal void ApplyLuminanceAndSaturation(ref Vector3 color)
    {
        if (!_hasGain)
        {
            return;
        }

        var hsv = Hsv.FromRgb(color);
        hsv.Z = MathF.Min(hsv.Z * _luminanceGain, 1.0f);
        hsv.Y = MathF.Min(hsv.Y * _saturationGain, 1.0f);
        color = Vector3.Clamp(Hsv.ToRgb(hsv), Vector3.Zero, Vector3.One);
    }

    internal void ApplyMinimalBacklight(ref Vector3 color)
    {
        if (!_hasBacklight)
        {
            return;
        }

        if (_coloredBacklight)
        {
            color = Vector3.Max(color, new Vector3(_minimalBacklight));
        }
        else if ((color.X + color.Y + color.Z) / 3.0f < _minimalBacklight)
        {
            color = new Vector3(_minimalBacklight);
        }
    }

    internal void ApplyPowerLimit(Span<Vector3> colors)
    {
        if (!_hasPowerLimit)
        {
            return;
        }

        var totalPower = 0.0f;
        foreach (var c in colors)
        {
            totalPower += c.X + c.Y + c.Z;
        }

        var allowedPower = colors.Length * 3.0f * _powerLimit;
        if (totalPower > 0.0f && allowedPower < totalPower)
        {
            var scale = allowedPower / totalPower;
            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] *= scale;
            }
        }
    }

    // Summed in the same order as linalg::mul(mat, vec) so results match the C++ build bit for bit.
    private static float Dot(Vector3 row, Vector3 v) => (row.X * v.X) + (row.Y * v.Y) + (row.Z * v.Z);

    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => (a * (1 - t)) + (b * t);

    private static bool IsIdentityCube(ColorAdjustment a) =>
        a.Red == new Rgb8(255, 0, 0) && a.Green == new Rgb8(0, 255, 0) && a.Blue == new Rgb8(0, 0, 255) &&
        a.Cyan == new Rgb8(0, 255, 255) && a.Magenta == new Rgb8(255, 0, 255) && a.Yellow == new Rgb8(255, 255, 0) &&
        a.White == Rgb8.White && a.Black == Rgb8.Black;

    // Trilinear cube between the eight calibrated corners, indexed [red][green][blue] like the C++ table.
    private static Vector3[] BuildCalibrationLut(ColorAdjustment a)
    {
        const int N = CalibrationLutDimension;
        var lut = new Vector3[N * N * N];
        var r = a.Red.ToVector();
        var g = a.Green.ToVector();
        var b = a.Blue.ToVector();
        var c = a.Cyan.ToVector();
        var m = a.Magenta.ToVector();
        var y = a.Yellow.ToVector();
        var w = a.White.ToVector();
        var k = a.Black.ToVector();

        for (var iz = 0; iz < N; iz++)
        {
            var tz = (float)iz / (N - 1);
            var kr = Lerp(k, r, tz);
            var gy = Lerp(g, y, tz);
            var bm = Lerp(b, m, tz);
            var cw = Lerp(c, w, tz);
            for (var iy = 0; iy < N; iy++)
            {
                var ty = (float)iy / (N - 1);
                var krGy = Lerp(kr, gy, ty);
                var bmCw = Lerp(bm, cw, ty);
                for (var ix = 0; ix < N; ix++)
                {
                    var tx = (float)ix / (N - 1);
                    lut[ix + (iy * N) + (iz * N * N)] = Lerp(krGy, bmCw, tx);
                }
            }
        }

        return lut;
    }

    private Vector3 SampleCalibrationLut(Vector3 color)
    {
        const int N = CalibrationLutDimension;
        var r = color.X * (N - 1);
        var g = color.Y * (N - 1);
        var b = color.Z * (N - 1);
        var r0 = (int)r;
        var g0 = (int)g;
        var b0 = (int)b;
        var r1 = Math.Min(r0 + 1, N - 1);
        var g1 = Math.Min(g0 + 1, N - 1);
        var b1 = Math.Min(b0 + 1, N - 1);
        var rFrac = r - r0;
        var gFrac = g - g0;
        var bFrac = b - b0;

        var lut = _calibrationLut;
        var c00 = Lerp(lut[b0 + (g0 * N) + (r0 * N * N)], lut[b1 + (g0 * N) + (r0 * N * N)], bFrac);
        var c10 = Lerp(lut[b0 + (g1 * N) + (r0 * N * N)], lut[b1 + (g1 * N) + (r0 * N * N)], bFrac);
        var c01 = Lerp(lut[b0 + (g0 * N) + (r1 * N * N)], lut[b1 + (g0 * N) + (r1 * N * N)], bFrac);
        var c11 = Lerp(lut[b0 + (g1 * N) + (r1 * N * N)], lut[b1 + (g1 * N) + (r1 * N * N)], bFrac);
        var c0 = Lerp(c00, c10, gFrac);
        var c1 = Lerp(c01, c11, gFrac);
        return Lerp(c0, c1, rFrac);
    }
}
