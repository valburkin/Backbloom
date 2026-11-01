using System.Diagnostics;
using System.Globalization;
using Backbloom.Core.Imaging;

namespace Backbloom.Capture;

/// <summary>
/// A still picture as the frame source, for screenshots and demos where the lights should follow a known image
/// (development, like the synthetic pattern). Reads binary PPM (P6, up to 8 bits per channel). The picture is scaled
/// down once by the smallest whole factor that makes it at most <see cref="MaxWidth"/> pixels wide (averaging each
/// block, like the capture's downscale), then copied into every frame at the given rate without allocating.
/// </summary>
public sealed class ImageFrameSource : IFrameSource
{
    /// <summary>Widest frame produced, the width the screen capture scales to by default.</summary>
    public const int MaxWidth = 512;

    private readonly byte[] _pixels;
    private readonly FramePacer _pacer;
    private readonly string _label;
    private readonly double _framesPerSecond;

    /// <summary>Loads the picture from <paramref name="path"/>.</summary>
    public ImageFrameSource(string path, double framesPerSecond)
        : this(File.ReadAllBytes(path), Path.GetFileName(path), framesPerSecond)
    {
    }

    /// <summary>Uses the PPM file <paramref name="ppm"/>; <paramref name="label"/> names it in <see cref="Device"/>.</summary>
    public ImageFrameSource(ReadOnlySpan<byte> ppm, string label, double framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(label);
        var (width, height, maxValue, dataStart) = ParsePpm(ppm);
        var divide = (width + MaxWidth - 1) / MaxWidth;
        if (height < divide)
        {
            throw new InvalidDataException($"The picture is too wide for its height ({width}x{height}): it must be at least {divide} pixels tall.");
        }

        Width = width / divide;
        Height = height / divide;
        _pixels = Downscale(ppm[dataStart..], width, maxValue, divide, Width, Height);
        _pacer = new FramePacer(framesPerSecond);
        _label = label;
        _framesPerSecond = framesPerSecond;
    }

    public int Width { get; }

    public int Height { get; }

    public string Name => "Still image";

    public string Device => string.Create(CultureInfo.InvariantCulture, $"{_label} {Width}x{Height} @ {_framesPerSecond:0} fps");

    public (int Width, int Height) FrameSize => (Width, Height);

    public bool TryCapture(Frame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_pacer.WaitNext(cancellationToken))
        {
            return false;
        }

        Render(frame);
        frame.Timestamp = Stopwatch.GetTimestamp();
        return true;
    }

    /// <summary>Copies the picture into <paramref name="frame"/> (cropped if the frame is smaller).</summary>
    public void Render(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var w = Math.Min(frame.Width, Width) * Frame.BytesPerPixel;
        var rows = Math.Min(frame.Height, Height);
        var source = _pixels.AsSpan();
        var pixels = frame.Pixels;
        for (var y = 0; y < rows; y++)
        {
            source.Slice(y * Width * Frame.BytesPerPixel, w).CopyTo(pixels.Slice(y * frame.Stride, w));
        }
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Reads a binary PPM header: "P6", width, height and the maximum value (separated by whitespace, '#' comments
    /// allowed), one whitespace byte, then RGB triples. Returns where the pixels start.
    /// </summary>
    internal static (int Width, int Height, int MaxValue, int DataStart) ParsePpm(ReadOnlySpan<byte> file)
    {
        if (file.Length < 2 || file[0] != 'P' || file[1] != '6')
        {
            throw new InvalidDataException("Not a binary PPM picture (it must start with \"P6\").");
        }

        var at = 2;
        var width = ReadNumber(file, ref at);
        var height = ReadNumber(file, ref at);
        var maxValue = ReadNumber(file, ref at);
        if (width <= 0 || height <= 0 || maxValue is <= 0 or > 255)
        {
            throw new InvalidDataException($"Unsupported PPM picture: {width}x{height}, maximum value {maxValue} (8 bits per channel are supported).");
        }

        at++; // the single whitespace byte before the pixels
        var length = checked(width * height * 3);
        if (file.Length - at < length)
        {
            throw new InvalidDataException("The PPM picture is shorter than its size says.");
        }

        return (width, height, maxValue, at);
    }

    private static int ReadNumber(ReadOnlySpan<byte> file, ref int at)
    {
        while (at < file.Length && (char.IsWhiteSpace((char)file[at]) || file[at] == '#'))
        {
            if (file[at] == '#')
            {
                while (at < file.Length && file[at] != '\n')
                {
                    at++;
                }
            }
            else
            {
                at++;
            }
        }

        var start = at;
        var value = 0;
        while (at < file.Length && file[at] is >= (byte)'0' and <= (byte)'9' && at - start < 9)
        {
            value = (value * 10) + (file[at++] - '0');
        }

        return at == start ? throw new InvalidDataException("The PPM header is cut short.") : value;
    }

    /// <summary>Averages each <paramref name="divide"/> x <paramref name="divide"/> block of RGB into one BGRA pixel.</summary>
    private static byte[] Downscale(ReadOnlySpan<byte> source, int width, int maxValue, int divide, int targetWidth, int targetHeight)
    {
        var result = new byte[targetWidth * targetHeight * Frame.BytesPerPixel];
        var count = divide * divide;
        for (var ty = 0; ty < targetHeight; ty++)
        {
            for (var tx = 0; tx < targetWidth; tx++)
            {
                int r = 0, g = 0, b = 0;
                for (var dy = 0; dy < divide; dy++)
                {
                    var row = ((ty * divide) + dy) * width;
                    for (var dx = 0; dx < divide; dx++)
                    {
                        var o = (row + (tx * divide) + dx) * 3;
                        r += source[o];
                        g += source[o + 1];
                        b += source[o + 2];
                    }
                }

                var p = ((ty * targetWidth) + tx) * Frame.BytesPerPixel;
                result[p] = Scale(b, count, maxValue);
                result[p + 1] = Scale(g, count, maxValue);
                result[p + 2] = Scale(r, count, maxValue);
                result[p + 3] = 255;
            }
        }

        return result;
    }

    /// <summary>The block's average on 0-255; values above the file's maximum (malformed files) count as the maximum.</summary>
    private static byte Scale(int sum, int count, int maxValue) =>
        (byte)((((long)Math.Min(sum, count * maxValue) * 255) + (count * maxValue / 2)) / (count * maxValue));
}
