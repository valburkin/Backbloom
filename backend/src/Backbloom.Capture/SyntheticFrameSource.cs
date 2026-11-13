using System.Diagnostics;
using Backbloom.Core.Color;
using Backbloom.Core.Imaging;

namespace Backbloom.Capture;

/// <summary>Options of the synthetic source; 512x288 at 60 fps resembles the reference setup's DX capture (512 px wide).</summary>
public sealed record SyntheticFrameSourceOptions(int Width = 512, int Height = 288, double FramesPerSecond = 60, double CycleSeconds = 8);

/// <summary>
/// Test pattern for development without a capture device: a diagonal rainbow that scrolls one full hue cycle every
/// <see cref="SyntheticFrameSourceOptions.CycleSeconds"/>. Every row is a copy from one precomputed palette, so
/// rendering costs little more than a memory copy and doesn't allocate.
/// </summary>
public sealed class SyntheticFrameSource : IFrameSource
{
    private readonly SyntheticFrameSourceOptions _options;
    private readonly FramePacer _pacer;
    private readonly byte[] _palette;
    private readonly long _started = Stopwatch.GetTimestamp();

    public SyntheticFrameSource(SyntheticFrameSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Height);
        _options = options;
        _pacer = new FramePacer(options.FramesPerSecond);

        // Two hue cycles side by side, so any window of `Width` pixels is one contiguous copy.
        var w = options.Width;
        _palette = new byte[w * 2 * Frame.BytesPerPixel];
        for (var x = 0; x < w * 2; x++)
        {
            var rgb = Hsv.ToRgb(new System.Numerics.Vector3(360f * (x % w) / w, 1, 1));
            var o = x * Frame.BytesPerPixel;
            _palette[o] = ColorQuantizer.ToByte(rgb.Z);
            _palette[o + 1] = ColorQuantizer.ToByte(rgb.Y);
            _palette[o + 2] = ColorQuantizer.ToByte(rgb.X);
            _palette[o + 3] = 255;
        }
    }

    public string Name => "Synthetic test pattern";

    public string Device => $"Rainbow {_options.Width}x{_options.Height} @ {_options.FramesPerSecond:0} fps";

    public (int Width, int Height) FrameSize => (_options.Width, _options.Height);

    public bool TryCapture(Frame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_pacer.WaitNext(cancellationToken))
        {
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        Render(frame, Stopwatch.GetElapsedTime(_started, now).TotalSeconds);
        frame.Timestamp = now;
        return true;
    }

    /// <summary>Draws the pattern as it looks <paramref name="seconds"/> after start.</summary>
    public void Render(Frame frame, double seconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var w = Math.Min(frame.Width, _options.Width);
        var shift = (int)(seconds / _options.CycleSeconds % 1.0 * _options.Width);
        var pixels = frame.Pixels;
        var palette = _palette.AsSpan();
        for (var y = 0; y < frame.Height; y++)
        {
            var start = (shift + (y * _options.Width / Math.Max(1, frame.Height))) % _options.Width;
            palette.Slice(start * Frame.BytesPerPixel, w * Frame.BytesPerPixel).CopyTo(pixels.Slice(y * frame.Stride, w * Frame.BytesPerPixel));
        }
    }

    public void Dispose()
    {
    }
}
