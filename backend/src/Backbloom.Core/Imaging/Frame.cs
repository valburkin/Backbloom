namespace Backbloom.Core.Imaging;

/// <summary>
/// One captured image in BGRA8 order (byte 0 = blue, 1 = green, 2 = red, 3 = alpha), the native layout of DXGI
/// desktop duplication. The pixel buffer is allocated once and reused; rent frames from a <see cref="FramePool"/>.
/// </summary>
public sealed class Frame
{
    public const int BytesPerPixel = 4;

    private byte[] _buffer;

    public Frame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _buffer = new byte[checked(width * height * BytesPerPixel)];
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Bytes per row (no padding: width * 4).</summary>
    public int Stride => Width * BytesPerPixel;

    public Span<byte> Pixels => _buffer.AsSpan(0, Stride * Height);

    public ReadOnlySpan<byte> ReadOnlyPixels => _buffer.AsSpan(0, Stride * Height);

    /// <summary>Monotonic capture time (<see cref="System.Diagnostics.Stopwatch"/> ticks); set by the frame source.</summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Changes the frame size, reusing the buffer when it is large enough. Allocates only when it grows.
    /// </summary>
    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var needed = checked(width * height * BytesPerPixel);
        if (needed > _buffer.Length)
        {
            _buffer = new byte[needed];
        }

        Width = width;
        Height = height;
    }
}
