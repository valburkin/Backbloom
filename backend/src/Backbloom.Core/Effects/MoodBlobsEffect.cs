using Backbloom.Core.Color;

namespace Backbloom.Core.Effects;

/// <summary>
/// Five slowly rotating color blobs whose base hue drifts within a range. Port of <c>Animation_MoodBlobs</c>
/// (sources/effects/Animation_MoodBlobs.cpp), presets "Full color mood blobs" (random start hue, the whole color
/// wheel) and "Warm mood blobs" (red to yellow).
/// </summary>
/// <remarks>
/// The C++ effect normalized its parameters (degrees to 0..1, the change rate to steps) each time the LED count
/// changed, so a second layout change divided them again; here they are normalized once. Its "reverse" option was
/// always off and is left out.
/// </remarks>
public sealed class MoodBlobsEffect : Effect
{
    private const double RotationTime = 60.0;
    private const int Blobs = 5;
    private const double SleepTime = 0.1;

    private readonly IEffectRandom _random;
    private readonly bool _colorRandom;
    private readonly double _hueChange;
    private readonly double _rangeLeft;
    private readonly double _rangeRight;
    private readonly double _changeRate;
    private readonly bool _fullColorWheel;
    private readonly ushort _baseHue;
    private readonly byte _baseSaturation;
    private readonly byte _baseValue;
    private readonly bool _baseColorChange;
    private Rgb8[] _colorData = [];
    private double _increase;
    private double _amplitudePhase;
    private bool _rotateColors;
    private int _changeStepCount;
    private double _baseHsvValue;
    private int _rotations;

    public MoodBlobsEffect(Parameters parameters, IEffectRandom random)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
        _colorRandom = parameters.ColorRandom;
        var left = parameters.BaseColorRangeLeft;
        var right = parameters.BaseColorRangeRight;
        _baseColorChange = !((right > left && right - left < 10) || (left > right && right + 360 - left < 10));
        _fullColorWheel = right % 360 == left % 360;
        _hueChange = Math.Max(0.0, Math.Min(Math.Abs(30.0 / 360.0), .5));
        _rangeLeft = left / 360.0;
        _rangeRight = right / 360.0;
        _changeRate = Math.Max(0.0, parameters.BaseColorChangeRate) / SleepTime;
        Hsv8.FromRgb(parameters.Color, out _baseHue, out _baseSaturation, out _baseValue);
    }

    public override EffectOutput Render(EffectCanvas canvas, Span<Rgb8> leds)
    {
        var count = leds.Length;
        if (count != _colorData.Length && count > 1)
        {
            Start(count);
        }

        if (_colorData.Length == 0 || count != _colorData.Length)
        {
            return EffectOutput.Leds;
        }

        if (_baseColorChange)
        {
            if (_changeStepCount >= _changeRate)
            {
                _changeStepCount = 0;
                if (_fullColorWheel)
                {
                    _baseHsvValue = (_baseHsvValue + _increase) % _rangeRight;
                }
                else
                {
                    if (_increase < 0 && _baseHsvValue > _rangeLeft && _baseHsvValue + _increase <= _rangeLeft)
                    {
                        _increase = Math.Abs(_increase);
                    }
                    else if (_increase > 0 && _baseHsvValue < _rangeRight && _baseHsvValue + _increase >= _rangeRight)
                    {
                        _increase = -Math.Abs(_increase);
                    }

                    _baseHsvValue = (_baseHsvValue + _increase) % 1.0;
                }

                FillColors(_baseHsvValue);
                for (var i = 0; i < _rotations; i++)
                {
                    RotateColors();
                }
            }

            _changeStepCount++;
        }

        for (var i = 0; i < count; i++)
        {
            var amplitude = Math.Max(0.0, Math.Sin(-_amplitudePhase + (2 * Math.PI * Blobs * i / (double)count)));
            var c = _colorData[i];
            leds[i] = new Rgb8((byte)(int)(c.R * amplitude), (byte)(int)(c.G * amplitude), (byte)(int)(c.B * amplitude));
        }

        _amplitudePhase = (_amplitudePhase + (Blobs * Math.PI * SleepTime / RotationTime)) % (2 * Math.PI);
        if (_rotateColors)
        {
            RotateColors();
            _rotations = (_rotations + 1) % count;
        }

        _rotateColors = !_rotateColors;
        return EffectOutput.Leds;
    }

    private void Start(int count)
    {
        var baseHsvX = _baseHue / 360.0;
        if (_colorRandom)
        {
            // the C++ code assigned degrees here (not 0..1); only the fraction matters for the hues
            baseHsvX = (int)(360.0 * (float)_random.Rand() / (float)MsvcRandom.Max);
        }

        _colorData = new Rgb8[count];
        FillColors(baseHsvX);
        _increase = 1.0 / 360.0;
        _amplitudePhase = 0.0;
        _rotateColors = false;
        _changeStepCount = 0;
        _baseHsvValue = baseHsvX;
        _rotations = 0;
        SleepTimeMs = (int)Math.Round(SleepTime * 1000.0, MidpointRounding.AwayFromZero);
    }

    private void FillColors(double baseHsv)
    {
        var count = _colorData.Length;
        for (var i = 0; i < count; i++)
        {
            var hue = (int)(360.0 * ((baseHsv + (_hueChange * Math.Sin(2 * Math.PI * i / count))) % 1.0));
            while (hue < 0)
            {
                hue += 360;
            }

            _colorData[i] = Hsv8.ToRgb((ushort)hue, _baseSaturation, _baseValue);
        }
    }

    // The last color moves to the front (C++: pop_back + insert at begin).
    private void RotateColors()
    {
        var last = _colorData[^1];
        Array.Copy(_colorData, 0, _colorData, 1, _colorData.Length - 1);
        _colorData[0] = last;
    }

    /// <param name="Color">The base color (its saturation and value are kept; the hue drifts).</param>
    /// <param name="ColorRandom">Start from a random hue instead of the base color's.</param>
    /// <param name="BaseColorRangeLeft">Start of the hue range the base hue drifts in (degrees).</param>
    /// <param name="BaseColorRangeRight">End of that range; equal to the start (mod 360): the whole color wheel.</param>
    /// <param name="BaseColorChangeRate">Seconds per one-degree step of the drift.</param>
    public sealed record Parameters(Rgb8 Color, bool ColorRandom, double BaseColorRangeLeft, double BaseColorRangeRight, double BaseColorChangeRate);
}
