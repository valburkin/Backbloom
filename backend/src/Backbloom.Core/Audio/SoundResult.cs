using Backbloom.Core.Color;

namespace Backbloom.Core.Audio;

/// <summary>
/// The moving average color the music effects show and how it moves toward its target (C++ <c>MovingTarget</c>, the
/// "average" track; the "slow" and "fast" tracks no kept effect read are left out).
/// </summary>
internal struct MovingTarget
{
    public Rgb8 AverageColor;
    public int TargetR;
    public int TargetG;
    public int TargetB;
    public int Counter;
}

/// <summary>What a music effect reads from one analysis result (C++ <c>SoundCaptureResult::GetStats</c> and friends).</summary>
/// <param name="Valid">The analysis has a level reference for every band yet (C++ <c>_validData</c>).</param>
/// <param name="ScaledAverage">Loudness 0..255.</param>
/// <param name="OldScaledAverage">The loudness of the previous result.</param>
/// <param name="AverageColor">The color of the spectrum (bass red ... treble blue), scaled by loudness.</param>
/// <param name="BandLevels">The eight band levels 0..255, band <c>i</c> in byte <c>i</c> (see <see cref="Band"/>).</param>
/// <param name="Beats">Beats detected since the capture started; an effect reacts when it changes.</param>
public readonly record struct MusicStats(bool Valid, int ScaledAverage, int OldScaledAverage, int CurrentMax, Rgb8 AverageColor, ulong BandLevels = 0, uint Beats = 0)
{
    /// <summary>Number of frequency bands, 0-86 Hz (bass) ... 6-11 kHz (treble).</summary>
    public const int BandCount = 8;

    /// <summary>The level of band <paramref name="index"/> (0..7), 0..255, relative to that band's recent maximum.</summary>
    public byte Band(int index) => (byte)(BandLevels >> (index * 8));

    /// <summary>
    /// The loudness a third (<paramref name="isMulti"/> = 2), two thirds (1) or all the way (0) from the previous
    /// result to this one (C++ <c>getValue3Step</c>).
    /// </summary>
    public int Value3Step(int isMulti) => isMulti switch
    {
        2 => Limit(((ScaledAverage - OldScaledAverage) / 3) + OldScaledAverage, 255),
        1 => Limit((((ScaledAverage - OldScaledAverage) * 2) / 3) + OldScaledAverage, 255),
        _ => ScaledAverage,
    };

    /// <summary>Raises the HSV value of <paramref name="color"/> to at least <paramref name="scale"/> (C++ <c>RestoreFullLum</c>).</summary>
    public static Rgb8 RestoreFullLum(Rgb8 color, int scale = 32)
    {
        Hsv8.FromRgb(color, out var hue, out var saturation, out var value);
        return value < scale ? Hsv8.ToRgb(hue, saturation, (byte)scale) : color;
    }

    internal static int Limit(int x, int y) => Math.Min(Math.Max(x, 0), y);
}

/// <summary>
/// Turns the band levels of each sound block into a loudness and a color that moves smoothly between blocks. Port of
/// <c>SoundCaptureResult</c> (sources/base/SoundCaptureResult.cpp, MIT, awawa-dev) with its integer arithmetic,
/// including the 8-bit wrap-around of the decaying band levels. The beat detection is new (HyperHDR had none). Not
/// thread-safe (<see cref="MusicAnalysis"/> locks).
/// </summary>
internal sealed class SoundResult
{
    public const int Bands = MusicStats.BandCount;

    // At most one beat per 5 blocks (230 ms at 21.5 blocks/s, up to 260 BPM).
    private const int MinBlocksBetweenBeats = 5;

    private static readonly Rgb8[] s_bandColors =
    [
        new(255, 0, 0), new(190, 0, 0), new(160, 160, 0), new(50, 190, 20),
        new(20, 255, 50), new(0, 212, 160), new(0, 120, 190), new(0, 0, 255),
    ];

    private readonly int[] _pure = new int[Bands];
    private readonly int[] _last = new int[Bands];
    private readonly int[] _max = new int[Bands];
    private readonly byte[] _deltas = new byte[Bands];
    private readonly byte[] _scaled = new byte[Bands];
    private int _maxAverage;
    private bool _validData;
    private int _averageDelta;
    private Rgb8 _lastPrevColor;
    private int _scaledAverage;
    private int _oldScaledAverage;
    private int _currentMax;
    private MovingTarget _internal;
    private int _bassAverage;
    private int _blocksSinceBeat;
    private uint _beats;

    public SoundResult() => Reset();

    /// <summary>Counts the results that had valid data; a music effect redraws when it changed.</summary>
    public uint ResultIndex { get; private set; }

    /// <summary>The track as the effects last moved it between two results (C++ <c>mtWorking</c>).</summary>
    public MovingTarget Working;

    /// <summary>The track as the last result left it (C++ <c>mtInternal</c>).</summary>
    public MovingTarget Internal => _internal;

    public MusicStats Stats
    {
        get
        {
            ulong bands = 0;
            for (var i = 0; i < Bands; i++)
            {
                bands |= (ulong)_scaled[i] << (i * 8);
            }

            return new(_validData, _scaledAverage, _oldScaledAverage, _currentMax, Working.AverageColor, bands, _beats);
        }
    }

    public void Reset()
    {
        _validData = false;
        _maxAverage = 0;
        _scaledAverage = 0;
        _oldScaledAverage = 0;
        ResultIndex = 0;
        Array.Clear(_pure);
        Array.Clear(_last);
        Array.Clear(_scaled);
        Array.Clear(_max);
        _deltas.AsSpan().Fill(10);
        _averageDelta = 10;
        _lastPrevColor = Rgb8.Black;
        _currentMax = 0;
        Working = default;
        _internal = default;
        _bassAverage = 0;
        _blocksSinceBeat = 0;
        _beats = 0;
    }

    public void ClearBands() => Array.Clear(_pure);

    public void AddToBand(int band, uint value) => _pure[band] = unchecked((int)((uint)_pure[band] + value));

    /// <summary>Takes the bands of a block into the levels, loudness and color (C++ <c>Smooth</c>).</summary>
    public void Smooth()
    {
        DetectBeat(_pure[0] + _pure[1]);
        var currentAverage = 0;
        var res = _internal;
        _validData = true;
        for (var i = 0; i < Bands; i++)
        {
            var buffResult = (_pure[i] + _last[i]) / 2;
            if (_max[i] <= buffResult && buffResult > 400)
            {
                if (_max[i] == 0)
                {
                    _validData = false;
                }

                _max[i] = buffResult;
                for (var j = 0; j < Bands; j++)
                {
                    if (i != j && _max[j] < buffResult / 4 && _max[j] < 400)
                    {
                        _max[j] = buffResult / 4;
                    }
                }
            }

            if (_validData && _max[i] > 0)
            {
                var pureScaled = (byte)Math.Min(unchecked(_pure[i] * 255) / _max[i], 255);
                if (pureScaled >= _scaled[i] || _scaled[i] - _deltas[i] < pureScaled)
                {
                    _scaled[i] = pureScaled;
                    _deltas[i] = (byte)Math.Max(pureScaled / 10, 3);
                }
                else
                {
                    _scaled[i] = unchecked((byte)(_scaled[i] - _deltas[i]));
                    _deltas[i] = (byte)Math.Min(_deltas[i] * 3 / 2, 255);
                }
            }

            _last[i] = _pure[i];
        }

        for (var j = 0; j < Bands; j++)
        {
            if (_max[j] > 2000)
            {
                _max[j] -= 2;
            }

            currentAverage += _scaled[j];
        }

        if (_validData)
        {
            if (currentAverage > _maxAverage)
            {
                _maxAverage = currentAverage;
            }

            if (_maxAverage > 0)
            {
                Update(currentAverage, ref res);
            }

            if (_maxAverage > 1024)
            {
                _maxAverage--;
            }
        }

        _internal = res;
        Working = res;
        if (_validData)
        {
            ResultIndex++;
        }
    }

    /// <summary>Moves the working track half a step toward its target (C++ <c>hasMiddleAverage</c>).</summary>
    public bool HasMiddleAverage(int middle)
    {
        if (Working.Counter <= 0)
        {
            return false;
        }

        var c = Working.AverageColor;
        Working.AverageColor = new Rgb8(
            (byte)MusicStats.Limit((Working.TargetR / 2) + c.R, 255),
            (byte)MusicStats.Limit((Working.TargetG / 2) + c.G, 255),
            (byte)MusicStats.Limit((Working.TargetB / 2) + c.B, 255));
        if (middle == 0)
        {
            Working.Counter--;
        }

        return true;
    }

    // A beat: the bass (0-258 Hz) of a block well above its recent average (about the last 8 blocks).
    private void DetectBeat(int bass)
    {
        _blocksSinceBeat++;
        if (bass > 400 && (long)bass * 10 > (long)_bassAverage * 14 && _blocksSinceBeat >= MinBlocksBetweenBeats)
        {
            _beats++;
            _blocksSinceBeat = 0;
        }

        _bassAverage += (bass - _bassAverage) / 8;
    }

    // C++ Drive: a step toward the target, at least 1 and at most distance / y.
    private static int Drive(int x, int y) => x == 0 ? 0 : x >= 0 && x < y ? 1 : x < 0 && -x < y ? -1 : x / y;

    private void Update(int currentAverage, ref MovingTarget res)
    {
        _currentMax = 0;
        for (var i = 0; i < Bands; i++)
        {
            if (_scaled[i] > _currentMax)
            {
                _currentMax = _scaled[i];
            }
        }

        currentAverage = Math.Min(currentAverage * 255 / _maxAverage, 255);
        currentAverage = Math.Min((currentAverage + _currentMax) / 2, 255);
        _oldScaledAverage = _scaledAverage;
        if (currentAverage >= _scaledAverage || _scaledAverage - _averageDelta < currentAverage)
        {
            _scaledAverage = currentAverage;
            _averageDelta = Math.Max(currentAverage / 14, 3);
        }
        else
        {
            _scaledAverage -= _averageDelta;
            _averageDelta = Math.Min(_averageDelta * 2, 255);
        }

        int r = 0, g = 0, b = 0;
        for (var i = 0; i < Bands && _currentMax > 0; i++)
        {
            var weight = _currentMax - (5 * (_currentMax - _scaled[i]));
            var color = s_bandColors[i];
            r += Math.Max(color.R * weight / _currentMax, 0);
            g += Math.Max(color.G * weight / _currentMax, 0);
            b += Math.Max(color.B * weight / _currentMax, 0);
        }

        var ccScale = Math.Max(Math.Max(r, g), b);
        if (ccScale != 0)
        {
            r = MusicStats.Limit((r * 255 / ccScale) + 32, 255) * _scaledAverage / 255;
            g = MusicStats.Limit((g * 255 / ccScale) + 32, 255) * _scaledAverage / 255;
            b = MusicStats.Limit((b * 255 / ccScale) + 32, 255) * _scaledAverage / 255;
        }

        var currentColor = new Rgb8((byte)r, (byte)g, (byte)b);
        var selected = res.AverageColor;
        var avR = (((currentColor.R * 2) + _lastPrevColor.R) / 3) - selected.R;
        var avG = (((currentColor.G * 2) + _lastPrevColor.G) / 3) - selected.G;
        var avB = (((currentColor.B * 2) + _lastPrevColor.B) / 3) - selected.B;
        _lastPrevColor = currentColor;

        if (res.Counter-- <= 0)
        {
            var distance = MusicStats.Limit((int)Math.Sqrt((avR * avR) + (avG * avG) + (avB * avB)), 255);
            distance = Math.Max(distance, 22) / 11;
            res.TargetR = Drive(avR, distance);
            res.TargetG = Drive(avG, distance);
            res.TargetB = Drive(avB, distance);
            res.Counter = Math.Abs(res.TargetR) >= 3 || Math.Abs(res.TargetG) >= 3 || Math.Abs(res.TargetB) >= 3 ? 3
                : Math.Abs(res.TargetR) >= 2 || Math.Abs(res.TargetG) >= 2 || Math.Abs(res.TargetB) >= 2 ? 1
                : 0;
        }

        res.AverageColor = new Rgb8(
            (byte)MusicStats.Limit(res.TargetR + selected.R, 255),
            (byte)MusicStats.Limit(res.TargetG + selected.G, 255),
            (byte)MusicStats.Limit(res.TargetB + selected.B, 255));
    }
}
