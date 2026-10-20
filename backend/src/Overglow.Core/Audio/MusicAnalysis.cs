using Overglow.Core.Effects;

namespace Overglow.Core.Audio;

/// <summary>
/// The sound analysis behind music mode: each block of 1024 mono 16-bit samples at 22,050 Hz goes through a
/// fixed-point FFT into eight frequency bands (0-86 Hz ... 6-11 kHz), then <see cref="SoundResult"/> turns them into a
/// loudness and a color. The music effects poll it from the frame loop. Port of <c>SoundCapture::analyzeSpectrum</c>
/// and <c>hasResult</c> (sources/base/SoundCapture.cpp, MIT, awawa-dev). Thread-safe (one lock, as the C++ mutex);
/// neither side allocates.
/// </summary>
public sealed class MusicAnalysis
{
    /// <summary>Samples per analyzed block.</summary>
    public const int BlockSize = FixedPointFft.Size;

    /// <summary>The sample rate the bands are laid out for (HyperHDR captured at 22,050 Hz).</summary>
    public const int SampleRate = 22050;

    // FFT bins per band (21.5 Hz each); the last band takes the rest of the 512 bins.
    private static readonly int[] s_bandBins = [4, 8, 12, 24, 48, 96, 96, 512];

    private readonly Lock _lock = new();
    private readonly SoundResult _result = new();
    private readonly short[] _real = new short[BlockSize];
    private readonly short[] _imaginary = new short[BlockSize];
    private bool _running;
    private bool _smoothing = true;

    /// <summary>Blocks are analyzed only while a capture runs (C++ <c>_isRunning</c>).</summary>
    public bool Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// The <c>soundEffect.enable_smoothing</c> setting: between two blocks the effects get two interpolated steps.
    /// </summary>
    public bool Smoothing
    {
        get
        {
            lock (_lock)
            {
                return _smoothing;
            }
        }

        set
        {
            lock (_lock)
            {
                _smoothing = value;
            }
        }
    }

    /// <summary>A capture starts: the levels learned so far are forgotten (C++ <c>open</c> -> <c>ResetData</c>).</summary>
    public void Start()
    {
        lock (_lock)
        {
            _result.Reset();
            _running = true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
        }
    }

    /// <summary>Analyzes one block of <see cref="BlockSize"/> samples.</summary>
    /// <param name="silent">True when every sample was within +-8 (the bands are zero then).</param>
    /// <returns>False when no capture runs (the block is ignored).</returns>
    public bool Analyze(ReadOnlySpan<short> samples, out bool silent)
    {
        if (samples.Length != BlockSize)
        {
            throw new ArgumentException($"A block has {BlockSize} samples.", nameof(samples));
        }

        silent = true;
        foreach (var s in samples)
        {
            if (s is < -8 or > 8)
            {
                silent = false;
                break;
            }
        }

        lock (_lock)
        {
            if (!_running)
            {
                return false;
            }

            _result.ClearBands();
            if (!silent)
            {
                samples.CopyTo(_real);
                Array.Clear(_imaginary);
                FixedPointFft.Forward(_real, _imaginary);
                for (int i = 0, band = 0, count = 0; i < BlockSize / 2; i++)
                {
                    var magnitude = (uint)Math.Sqrt((_real[i] * _real[i]) + (_imaginary[i] * _imaginary[i]));
                    _result.AddToBand(band, magnitude);
                    count++;
                    if (band < SoundResult.Bands - 1 && count >= s_bandBins[band])
                    {
                        count = 0;
                        band++;
                    }
                }
            }

            _result.Smooth();
            return true;
        }
    }

    /// <summary>
    /// What a music effect should draw now (C++ <c>hasResult</c>): the newest result once (<paramref name="isMulti"/>
    /// becomes 2), then, with smoothing, two interpolated steps toward it, then nothing until the next block.
    /// </summary>
    /// <param name="lastIndex">The result the effect drew last; updated.</param>
    /// <param name="newAverage">The color moved (draw a new picture).</param>
    /// <param name="isMulti">Steps left until the next result; updated.</param>
    /// <returns>False when there is nothing to draw.</returns>
    internal bool HasResult(MusicEffect effect, ref uint lastIndex, out bool newAverage, ref int isMulti, out MusicStats stats)
    {
        lock (_lock)
        {
            if (lastIndex != _result.ResultIndex)
            {
                lastIndex = _result.ResultIndex;
                effect.Target = _result.Internal;
                isMulti = 2;
                newAverage = true;
                stats = _result.Stats;
                return true;
            }

            _result.Working = effect.Target;
            if (isMulti <= 0 || !_smoothing)
            {
                newAverage = false;
                stats = default;
                return false;
            }

            isMulti--;
            newAverage = _result.HasMiddleAverage(isMulti);
            effect.Target = _result.Working;
            stats = _result.Stats;
            return true;
        }
    }
}
