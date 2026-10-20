using System.Buffers.Binary;
using Backbloom.Core.Audio;

namespace Backbloom.Audio;

/// <summary>
/// Turns captured audio of any rate, channel count and sample format into the blocks <see cref="MusicAnalysis"/>
/// analyzes: mono (channels averaged), 16-bit, 22,050 Hz (each output sample is the mean of the input over its time
/// span, which also filters what the lower rate can't hold), 1024 samples per block. HyperHDR had the Windows wave
/// mapper deliver that format directly. Used by one capture thread at a time; allocation-free per buffer.
/// </summary>
public sealed class AudioBlockWriter
{
    private readonly MusicAnalysis _analysis;
    private readonly short[] _block = new short[MusicAnalysis.BlockSize];
    private AudioFormat? _format;
    private int _filled;
    private double _step = 1;
    private double _need = 1;
    private double _sum;
    private long _blocks;
    private long _silentBlocks;

    public AudioBlockWriter(MusicAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        _analysis = analysis;
    }

    /// <summary>Blocks analyzed so far.</summary>
    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>Of those, the silent ones (every sample within +-8).</summary>
    public long SilentBlocks => Interlocked.Read(ref _silentBlocks);

    /// <summary>Raised on the capture thread after a block was analyzed, with whether it was silent.</summary>
    public event Action<bool>? BlockAnalyzed;

    /// <summary>The device delivers <paramref name="format"/> from now on (a partial block is dropped).</summary>
    public void Configure(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.SampleRate <= 0 || format.Channels <= 0)
        {
            throw new ArgumentException("Invalid audio format.", nameof(format));
        }

        _format = format;
        _step = format.SampleRate / (double)MusicAnalysis.SampleRate;
        _need = _step;
        _sum = 0;
        _filled = 0;
    }

    /// <summary>Takes <paramref name="frames"/> interleaved frames in the configured format.</summary>
    public void Write(ReadOnlySpan<byte> data, int frames)
    {
        var format = _format ?? throw new InvalidOperationException("Configure the format first.");
        var frameBytes = format.BytesPerFrame;
        frames = Math.Min(frames, data.Length / frameBytes);
        for (var f = 0; f < frames; f++)
        {
            var frame = data.Slice(f * frameBytes, frameBytes);
            double sum = 0;
            for (var c = 0; c < format.Channels; c++)
            {
                sum += Sample(frame, c, format);
            }

            Add(sum / format.Channels);
        }
    }

    /// <summary>Takes <paramref name="frames"/> frames of silence (a loopback device delivers nothing while nothing plays).</summary>
    public void WriteSilence(int frames)
    {
        for (var f = 0; f < frames; f++)
        {
            Add(0);
        }
    }

    // One sample as a 16-bit value (-32768..32767 as a double).
    private static double Sample(ReadOnlySpan<byte> frame, int channel, AudioFormat format) => format.SampleFormat switch
    {
        AudioSampleFormat.IeeeFloat => BinaryPrimitives.ReadSingleLittleEndian(frame[(channel * 4)..]) * 32767.0,
        AudioSampleFormat.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(frame[(channel * 2)..]),
        AudioSampleFormat.Pcm24 => ((frame[(channel * 3) + 2] << 24) | (frame[(channel * 3) + 1] << 16) | (frame[channel * 3] << 8)) / 65536.0,
        _ => BinaryPrimitives.ReadInt32LittleEndian(frame[(channel * 4)..]) / 65536.0,
    };

    // Area resampling: each input sample spans 1 unit of time, each output sample _step units.
    private void Add(double value)
    {
        var remaining = 1.0;
        while (remaining > 0)
        {
            var take = Math.Min(remaining, _need);
            _sum += value * take;
            _need -= take;
            remaining -= take;
            if (_need <= 1e-9)
            {
                Emit(_sum / _step);
                _sum = 0;
                _need += _step;
            }
        }
    }

    private void Emit(double value)
    {
        _block[_filled++] = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
        if (_filled < _block.Length)
        {
            return;
        }

        _filled = 0;
        if (_analysis.Analyze(_block, out var silent))
        {
            Interlocked.Increment(ref _blocks);
            if (silent)
            {
                Interlocked.Increment(ref _silentBlocks);
            }

            BlockAnalyzed?.Invoke(silent);
        }
    }
}
