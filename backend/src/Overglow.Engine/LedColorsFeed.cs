using System.Numerics;
using Overglow.Core.Color;

namespace Overglow.Engine;

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

    public void Publish(ReadOnlySpan<Vector3> colors)
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
        }

        Interlocked.Increment(ref _sequence);
    }

    /// <summary>Copies the newest frame (three bytes per LED) and returns the number of bytes written.</summary>
    public int CopyLatest(Span<byte> destination)
    {
        lock (_lock)
        {
            var n = Math.Min(destination.Length, _count * 3);
            _latest.AsSpan(0, n).CopyTo(destination);
            return n;
        }
    }
}
