using System.Numerics;
using Backbloom.Core.Color;

namespace Backbloom.Engine;

/// <summary>
/// The latest output colors (8-bit RGB, before any controller's color order) for the UI's live preview. The pipeline
/// publishes every frame without allocating; readers copy the newest frame at their own pace.
/// </summary>
public sealed class LedColorsFeed
{
    private readonly Lock _lock = new();
    private byte[] _latest = [];
    private byte[] _spare = [];
    private int _count;
    private float _share = 1.0f;
    private long _sequence;

    /// <summary>Increments with every published frame.</summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    public int LedCount
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>Publishes a frame as it goes to the controllers.</summary>
    /// <param name="colors">The colors sent (nonlinear 0..1).</param>
    /// <param name="share">
    /// The share of the full output Home's Brightness left in this frame (<see cref="Core.Color.ColorProcessor.OutputShare"/>),
    /// so a preview can make a dim frame readable without guessing the brightness.
    /// </param>
    public void Publish(ReadOnlySpan<Vector3> colors, float share = 1.0f)
    {
        var needed = colors.Length * 3;
        if (_spare.Length != needed)
        {
            _spare = new byte[needed];
        }

        ColorQuantizer.ToBytes(colors, ColorOrder.Rgb, _spare);
        lock (_lock)
        {
            (_latest, _spare) = (_spare, _latest);
            _count = colors.Length;
            _share = float.IsFinite(share) ? Math.Clamp(share, 0.0f, 1.0f) : 1.0f;
        }

        Interlocked.Increment(ref _sequence);
    }

    /// <summary>Copies the newest frame (three bytes per LED) and returns the number of bytes written.</summary>
    public int CopyLatest(Span<byte> destination) => CopyLatest(destination, out _);

    /// <summary>
    /// Copies the newest frame (three bytes per LED) with the share of the full output the brightness left in it, and
    /// returns the number of bytes written.
    /// </summary>
    public int CopyLatest(Span<byte> destination, out float share)
    {
        lock (_lock)
        {
            var n = Math.Min(destination.Length, _count * 3);
            _latest.AsSpan(0, n).CopyTo(destination);
            share = _share;
            return n;
        }
    }
}
