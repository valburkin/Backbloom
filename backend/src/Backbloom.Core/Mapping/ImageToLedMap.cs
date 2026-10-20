using System.Numerics;
using Backbloom.Core.Color;
using Backbloom.Core.Imaging;
using Backbloom.Core.Layout;

namespace Backbloom.Core.Mapping;

/// <summary>
/// Precomputed pixel offsets for one frame size, LED layout and mapping type, and the per-frame averaging that
/// uses them. Port of HyperHDR's <c>ImageColorAveraging</c> (sources/base/ImageColorAveraging.cpp), reading BGRA
/// frames instead of RGB ones.
/// </summary>
/// <remarks>
/// All buffers are built in the constructor; <see cref="Process"/> does not allocate. Averaging happens in linear
/// light: every sampled 8-bit sRGB value goes through a 16-bit linear lookup table before it is summed.
/// </remarks>
public sealed class ImageToLedMap
{
    /// <summary>Areas with more pixels than this are sampled every other row and column (as in the C++ code).</summary>
    public const int SparseThreshold = 1600;

    private readonly int[] _offsets;
    private readonly int[] _start;
    private readonly int[] _count;
    private readonly int[][] _groups;

    public ImageToLedMap(
        LedLayout layout,
        LedMappingType mappingType,
        bool sparseProcessing,
        int width,
        int height,
        int horizontalBorder = 0,
        int verticalBorder = 0)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(horizontalBorder);
        ArgumentOutOfRangeException.ThrowIfNegative(verticalBorder);
        if (width <= 2 * verticalBorder || height <= 2 * horizontalBorder)
        {
            throw new ArgumentException("The black borders leave no picture to sample.");
        }

        Layout = layout;
        MappingType = mappingType;
        SparseProcessing = sparseProcessing;
        Width = width;
        Height = height;
        HorizontalBorder = horizontalBorder;
        VerticalBorder = verticalBorder;

        var stride = width * Frame.BytesPerPixel;
        var xOffset = verticalBorder;
        var actualWidth = width - (2 * verticalBorder);
        var yOffset = horizontalBorder;
        var actualHeight = height - (2 * horizontalBorder);

        var perLed = new List<int>[layout.Count];
        var groups = new SortedDictionary<int, List<int>>();
        var autoSparse = new List<int>();

        for (var ledIndex = 0; ledIndex < layout.Count; ledIndex++)
        {
            var led = layout.Areas[ledIndex];
            var offsets = new List<int>();
            perLed[ledIndex] = offsets;
            if (led.IsEmpty)
            {
                continue;
            }

            var minX = xOffset + QtRound(actualWidth * led.HMin);
            var maxX = xOffset + QtRound(actualWidth * led.HMax);
            var minY = yOffset + QtRound(actualHeight * led.VMin);
            var maxY = yOffset + QtRound(actualHeight * led.VMax);

            minX = Math.Min(minX, xOffset + actualWidth - 1);
            if (minX == maxX)
            {
                maxX++;
            }

            minY = Math.Min(minY, yOffset + actualHeight - 1);
            if (minY == maxY)
            {
                maxY++;
            }

            var endY = Math.Min(maxY, yOffset + actualHeight);
            var endX = Math.Min(maxX, xOffset + actualWidth);
            var pixels = (long)Math.Abs(endY - minY) * Math.Abs(endX - minX);

            var sparse = sparseProcessing;
            if (!sparse && pixels > SparseThreshold)
            {
                sparse = true;
                autoSparse.Add(ledIndex);
            }

            var increment = sparse ? 2 : 1;
            if (!led.Disabled)
            {
                for (var y = minY; y < endY; y += increment)
                {
                    for (var x = minX; x < endX; x += increment)
                    {
                        offsets.Add((y * stride) + (x * Frame.BytesPerPixel));
                    }
                }
            }

            if (led.Group > 0)
            {
                if (groups.TryGetValue(led.Group, out var members))
                {
                    perLed[members[0]].AddRange(offsets);
                    offsets.Clear();
                }
                else
                {
                    members = [];
                    groups.Add(led.Group, members);
                }

                members.Add(ledIndex);
            }
        }

        _start = new int[layout.Count];
        _count = new int[layout.Count];
        var total = 0;
        for (var i = 0; i < perLed.Length; i++)
        {
            _start[i] = total;
            _count[i] = perLed[i].Count;
            total += perLed[i].Count;
        }

        _offsets = new int[total];
        for (var i = 0; i < perLed.Length; i++)
        {
            perLed[i].CopyTo(_offsets, _start[i]);
        }

        _groups = [.. groups.Values.Select(g => g.ToArray())];
        AutoSparseLeds = autoSparse;
        SampledPixels = total;
    }

    public LedLayout Layout { get; }

    public LedMappingType MappingType { get; }

    public bool SparseProcessing { get; }

    public int Width { get; }

    public int Height { get; }

    public int HorizontalBorder { get; }

    public int VerticalBorder { get; }

    /// <summary>LEDs whose area was so large that sparse sampling was switched on for them.</summary>
    public IReadOnlyList<int> AutoSparseLeds { get; }

    /// <summary>Total number of pixel samples per frame (advanced mapping).</summary>
    public int SampledPixels { get; }

    /// <summary>
    /// Averages the frame into one linear-light color (0..1) per LED. Port of <c>ImageColorAveraging::process</c>.
    /// </summary>
    /// <param name="bgra">Frame pixels: <see cref="Width"/> x <see cref="Height"/>, 4 bytes per pixel, no padding.</param>
    /// <param name="ledColors">Receives <c>Layout.Count</c> colors.</param>
    public void Process(ReadOnlySpan<byte> bgra, Span<Vector3> ledColors)
    {
        if (bgra.Length < Width * Height * Frame.BytesPerPixel)
        {
            throw new ArgumentException("The frame is smaller than the mapping.", nameof(bgra));
        }

        if (ledColors.Length < Layout.Count)
        {
            throw new ArgumentException("The output must hold one color per LED.", nameof(ledColors));
        }

        if (MappingType == LedMappingType.UnicolorMean)
        {
            ledColors[..Layout.Count].Fill(AverageWholeFrame(bgra));
            return;
        }

        var lut = Srgb.U8ToLinear16;
        for (var led = 0; led < Layout.Count; led++)
        {
            var count = _count[led];
            if (count == 0)
            {
                ledColors[led] = Vector3.Zero;
                continue;
            }

            long r = 0, g = 0, b = 0;
            foreach (var offset in _offsets.AsSpan(_start[led], count))
            {
                b += lut[bgra[offset]];
                g += lut[bgra[offset + 1]];
                r += lut[bgra[offset + 2]];
            }

            ledColors[led] = Average(r, g, b, count);
        }

        foreach (var group in _groups)
        {
            var combined = ledColors[group[0]];
            for (var i = 1; i < group.Length; i++)
            {
                ledColors[group[i]] = combined;
            }
        }
    }

    // Same rounding as qRound for the non-negative values used here.
    private static int QtRound(double value) => value >= 0.0 ? (int)(value + 0.5) : (int)(value - 0.5);

    private static Vector3 Average(long r, long g, long b, long count)
    {
        var n = (float)count;
        return new Vector3((float)r / n / 65535.0f, (float)g / n / 65535.0f, (float)b / n / 65535.0f);
    }

    private Vector3 AverageWholeFrame(ReadOnlySpan<byte> bgra)
    {
        var lut = Srgb.U8ToLinear16;
        var stride = Width * Frame.BytesPerPixel;
        var increment = SparseProcessing ? 2 : 1;
        long r = 0, g = 0, b = 0, count = 0;
        for (var y = 0; y < Height; y += increment)
        {
            var row = bgra.Slice(y * stride, stride);
            for (var o = 0; o < stride; o += increment * Frame.BytesPerPixel)
            {
                b += lut[row[o]];
                g += lut[row[o + 1]];
                r += lut[row[o + 2]];
                count++;
            }
        }

        return Average(r, g, b, count);
    }
}
