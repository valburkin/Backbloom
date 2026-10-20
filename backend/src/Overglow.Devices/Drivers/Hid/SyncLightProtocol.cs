namespace Overglow.Devices.Drivers.Hid;

/// <summary>
/// Robobloq SyncLight frames as built by <c>DriverHidSyncLight</c> (sources/led-drivers/hid/DriverHidSyncLight.cpp,
/// protocol notes in sources/led-drivers/hid/SyncLightProtocol.md): "RB" service frames (length, id, action, payload, additive
/// checksum) and "SC" per-segment frames (big-endian length, id, 0x80, ranges of {first LED, R, G, B, last LED},
/// checksum). Frames go out in 64-byte chunks, each in a 65-byte output report with report ID 0.
/// </summary>
/// <remarks>Allocation-free: the range merging works in buffers sized once per controller LED count.</remarks>
public sealed class SyncLightProtocol
{
    public const int ReportSize = 64;
    public const int MaxLeds = 254;
    public const int MaxRanges = 34;
    public const byte ActionColor = 0x86;
    public const byte ActionReadInfo = 0x82;
    public const byte ActionBrightness = 0x87;
    public const byte ActionStopEffect = 0x97;

    private Range[] _ranges = [];
    private byte[] _positions = [];
    private byte _id;

    /// <summary>Largest frame: an SC frame with 254 ranges.</summary>
    public const int MaxFrameSize = 7 + (5 * MaxLeds);

    /// <summary>The next frame id: 1..255, never 0.</summary>
    public byte NextId()
    {
        _id = (byte)((_id % 255) + 1);
        return _id;
    }

    /// <summary>Writes an RB frame and returns its length.</summary>
    public static int WriteRb(Span<byte> frame, byte action, ReadOnlySpan<byte> payload, byte id)
    {
        var length = 6 + payload.Length;
        frame[0] = (byte)'R';
        frame[1] = (byte)'B';
        frame[2] = (byte)length;
        frame[3] = id;
        frame[4] = action;
        payload.CopyTo(frame[5..]);
        frame[length - 1] = Checksum(frame[..(length - 1)]);
        return length;
    }

    /// <summary>The global color frame payload: section 1, the averaged color, the controller's last LED and its successor.</summary>
    public static int WriteGlobalColor(Span<byte> frame, ReadOnlySpan<byte> rgb, int controllerLeds, byte id)
    {
        var (r, g, b) = Average(rgb, 0, Math.Min(rgb.Length / 3, MaxLeds));
        var last = (byte)controllerLeds;
        ReadOnlySpan<byte> payload = [1, r, g, b, last, (byte)(last + 1), 0x00, 0x00, 0x00, 0xfe];
        return WriteRb(frame, ActionColor, payload, id);
    }

    /// <summary>
    /// The SC frame for one color per controller position (our LEDs averaged or repeated to the controller's count),
    /// with at most <paramref name="segmentLimit"/> ranges: the adjacent pair whose merge adds the least squared color
    /// error is merged first (leftmost on ties).
    /// </summary>
    public int WriteSegments(Span<byte> frame, ReadOnlySpan<byte> rgb, int controllerLeds, int segmentLimit, byte id)
    {
        var n = rgb.Length / 3;
        if (_positions.Length != controllerLeds * 3)
        {
            _positions = new byte[controllerLeds * 3];
            _ranges = new Range[controllerLeds];
        }

        for (var p = 0; p < controllerLeds; p++)
        {
            var first = p * n / controllerLeds;
            var last = Math.Max(first + 1, (p + 1) * n / controllerLeds);
            var (r, g, b) = Average(rgb, first, Math.Min(last, n));
            (_positions[p * 3], _positions[(p * 3) + 1], _positions[(p * 3) + 2]) = (r, g, b);
        }

        var count = controllerLeds;
        for (var i = 0; i < count; i++)
        {
            _ranges[i] = Range.Create(i + 1, i + 1, 1, _positions[i * 3], _positions[(i * 3) + 1], _positions[(i * 3) + 2]);
        }

        while (count > segmentLimit)
        {
            var best = 0;
            var bestCost = double.PositiveInfinity;
            for (var i = 0; i + 1 < count; i++)
            {
                var cost = _ranges[i].Energy + _ranges[i + 1].Energy - Range.Merge(_ranges[i], _ranges[i + 1]).Energy;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = i;
                }
            }

            _ranges[best] = Range.Merge(_ranges[best], _ranges[best + 1]);
            Array.Copy(_ranges, best + 2, _ranges, best + 1, count - best - 2);
            count--;
        }

        var length = 7 + (5 * count);
        frame[0] = (byte)'S';
        frame[1] = (byte)'C';
        frame[2] = (byte)(length >> 8);
        frame[3] = (byte)(length & 0xff);
        frame[4] = id;
        frame[5] = 0x80;
        for (var i = 0; i < count; i++)
        {
            var r = _ranges[i];
            var o = 6 + (i * 5);
            frame[o] = (byte)r.First;
            frame[o + 1] = r.Average(r.R);
            frame[o + 2] = r.Average(r.G);
            frame[o + 3] = r.Average(r.B);
            frame[o + 4] = (byte)r.Last;
        }

        frame[length - 1] = Checksum(frame[..(length - 1)]);
        return length;
    }

    /// <summary>Copies chunk <paramref name="index"/> of a frame into a 65-byte report (report ID 0, zero padded).</summary>
    public static void WriteReport(ReadOnlySpan<byte> frame, int index, Span<byte> report)
    {
        report[..(ReportSize + 1)].Clear();
        var offset = index * ReportSize;
        frame.Slice(offset, Math.Min(ReportSize, frame.Length - offset)).CopyTo(report[1..]);
    }

    public static int ReportCount(int frameLength) => (frameLength + ReportSize - 1) / ReportSize;

    /// <summary>The 8-bit sum of the bytes.</summary>
    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (var b in bytes)
        {
            sum += b;
        }

        return sum;
    }

    /// <summary>Truncating average of LEDs [first, last) (black when empty), as the C++ averageColor.</summary>
    private static (byte R, byte G, byte B) Average(ReadOnlySpan<byte> rgb, int first, int last)
    {
        if (first >= last)
        {
            return (0, 0, 0);
        }

        long r = 0, g = 0, b = 0;
        for (var i = first; i < last; i++)
        {
            r += rgb[i * 3];
            g += rgb[(i * 3) + 1];
            b += rgb[(i * 3) + 2];
        }

        var count = last - first;
        return ((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    /// <summary>A run of adjacent LEDs sent as one color; energy = |sum|^2 / count.</summary>
    private readonly record struct Range(int First, int Last, ulong Count, ulong R, ulong G, ulong B, double Energy)
    {
        public static Range Create(int first, int last, ulong count, ulong r, ulong g, ulong b) =>
            new(first, last, count, r, g, b, (double)((r * r) + (g * g) + (b * b)) / count);

        public static Range Merge(Range a, Range b) => Create(a.First, b.Last, a.Count + b.Count, a.R + b.R, a.G + b.G, a.B + b.B);

        /// <summary>Rounded average of one channel sum.</summary>
        public byte Average(ulong sum) => (byte)(((2 * sum) + Count) / (2 * Count));
    }
}
