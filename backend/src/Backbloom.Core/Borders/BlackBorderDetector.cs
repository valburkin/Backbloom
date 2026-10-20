using Backbloom.Core.Imaging;

namespace Backbloom.Core.Borders;

/// <summary>How the detector looks for the border (the <c>blackborderdetector.mode</c> setting).</summary>
public enum BlackBorderMode
{
    /// <summary>"default": three lines (at 33 %, 66 % and the center) from every side.</summary>
    Default,

    /// <summary>"classic": the diagonal from the top left corner, then left and up from the first lit pixel.</summary>
    Classic,

    /// <summary>"osd": columns first, then rows at the detected column (ignores overlays in the middle).</summary>
    Osd,

    /// <summary>"letterbox": rows only, from the top and the bottom at 25 % and 75 % and the top center.</summary>
    Letterbox,
}

public static class BlackBorderModes
{
    /// <summary>
    /// Parses the setting. The C++ processor detects nothing for unknown names; the settings schema only allows the
    /// four names, so anything else falls back to the default mode here.
    /// </summary>
    public static BlackBorderMode Parse(string? value) => value switch
    {
        "classic" => BlackBorderMode.Classic,
        "osd" => BlackBorderMode.Osd,
        "letterbox" => BlackBorderMode.Letterbox,
        _ => BlackBorderMode.Default,
    };

    public static string ToSettingString(this BlackBorderMode mode) => mode switch
    {
        BlackBorderMode.Classic => "classic",
        BlackBorderMode.Osd => "osd",
        BlackBorderMode.Letterbox => "letterbox",
        _ => "default",
    };
}

/// <summary>
/// Finds the black bars of a single frame. Port of <c>backbloom::BlackBorderDetector</c>
/// (sources/blackborder/BlackBorderDetector.cpp) on BGRA frames; it samples a few lines, so it is cheap and does not
/// allocate. <see cref="BlackBorderProcessor"/> adds the consistency rules that decide when a border is applied.
/// </summary>
public sealed class BlackBorderDetector
{
    /// <param name="threshold">Fraction 0..1 (the setting divided by 100); a pixel is black when all channels are below it.</param>
    public BlackBorderDetector(double threshold)
    {
        Threshold = CalculateThreshold(threshold);
    }

    /// <summary>The 8-bit limit: a pixel is black when red, green and blue are all below it.</summary>
    public byte Threshold { get; }

    /// <summary>ceil(threshold * 255) clamped to 0..255 (C++ <c>calculateThreshold</c>).</summary>
    public static byte CalculateThreshold(double threshold) => (byte)Math.Clamp((int)Math.Ceiling(threshold * 255), 0, 255);

    public BlackBorder Process(Frame frame, BlackBorderMode mode)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Process(frame.ReadOnlyPixels, frame.Width, frame.Height, mode);
    }

    /// <param name="bgra">width x height pixels, 4 bytes each, no row padding.</param>
    public BlackBorder Process(ReadOnlySpan<byte> bgra, int width, int height, BlackBorderMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length < width * height * Frame.BytesPerPixel)
        {
            throw new ArgumentException("The frame is smaller than its size.", nameof(bgra));
        }

        var image = new Pixels(bgra, width, Threshold);
        return mode switch
        {
            BlackBorderMode.Classic => ProcessClassic(image, width, height),
            BlackBorderMode.Osd => ProcessOsd(image, width, height),
            BlackBorderMode.Letterbox => ProcessLetterbox(image, width, height),
            _ => ProcessDefault(image, width, height),
        };
    }

    // test the center and 33 %, 66 % of width/height: 33 and 66 check left and top, the center checks right and bottom
    private static BlackBorder ProcessDefault(Pixels image, int width, int height)
    {
        var width33 = width / 3;
        var height33 = height / 3;
        var width66 = width33 * 2;
        var height66 = height33 * 2;
        var xCenter = width / 2;
        var yCenter = height / 2;
        var lastX = width - 1;
        var lastY = height - 1;

        var firstX = -1;
        for (var x = 0; x < width33; x++)
        {
            if (!image.IsBlack(lastX - x, yCenter) || !image.IsBlack(x, height33) || !image.IsBlack(x, height66))
            {
                firstX = x;
                break;
            }
        }

        var firstY = -1;
        for (var y = 0; y < height33; y++)
        {
            if (!image.IsBlack(xCenter, lastY - y) || !image.IsBlack(width33, y) || !image.IsBlack(width66, y))
            {
                firstY = y;
                break;
            }
        }

        return new BlackBorder(firstX == -1 || firstY == -1, firstY, firstX);
    }

    // only the top left third: walk the diagonal to the first lit pixel, then extend left and up
    private static BlackBorder ProcessClassic(Pixels image, int width, int height)
    {
        var thirdWidth = width / 3;
        var thirdHeight = height / 3;
        var maxSize = Math.Max(thirdWidth, thirdHeight);

        var firstX = -1;
        var firstY = -1;
        for (var i = 0; i < maxSize; i++)
        {
            var x = Math.Min(i, thirdWidth);
            var y = Math.Min(i, thirdHeight);
            if (!image.IsBlack(x, y))
            {
                firstX = x;
                firstY = y;
                break;
            }
        }

        for (; firstX > 0; firstX--)
        {
            if (image.IsBlack(firstX - 1, firstY))
            {
                break;
            }
        }

        for (; firstY > 0; firstY--)
        {
            if (image.IsBlack(firstX, firstY - 1))
            {
                break;
            }
        }

        return new BlackBorder(firstX == -1 || firstY == -1, firstY, firstX);
    }

    // rows only: 25 % and 75 % of the width from the top and the bottom, the center only from the top (captions)
    private static BlackBorder ProcessLetterbox(Pixels image, int width, int height)
    {
        var width25 = width / 4;
        var height33 = height / 3;
        var width75 = width25 * 3;
        var xCenter = width / 2;
        var lastY = height - 1;

        var firstY = -1;
        for (var y = 0; y < height33; y++)
        {
            if (!image.IsBlack(xCenter, y) || !image.IsBlack(width25, y) || !image.IsBlack(width75, y) ||
                !image.IsBlack(width25, lastY - y) || !image.IsBlack(width75, lastY - y))
            {
                firstY = y;
                break;
            }
        }

        return new BlackBorder(firstY == -1, firstY, 0);
    }

    // find the column first (33 %, 66 % from the left, the center from the right), then the rows at that column
    private static BlackBorder ProcessOsd(Pixels image, int width, int height)
    {
        var width33 = width / 3;
        var height33 = height / 3;
        var height66 = height33 * 2;
        var yCenter = height / 2;
        var lastX = width - 1;
        var lastY = height - 1;

        var firstX = -1;
        int x;
        for (x = 0; x < width33; x++)
        {
            if (!image.IsBlack(lastX - x, yCenter) || !image.IsBlack(x, height33) || !image.IsBlack(x, height66))
            {
                firstX = x;
                break;
            }
        }

        var firstY = -1;
        for (var y = 0; y < height33; y++)
        {
            if (!image.IsBlack(x, y) || !image.IsBlack(x, lastY - y) || !image.IsBlack(lastX - x, y) || !image.IsBlack(lastX - x, lastY - y))
            {
                firstY = y;
                break;
            }
        }

        return new BlackBorder(firstX == -1 || firstY == -1, firstY, firstX);
    }

    private readonly ref struct Pixels(ReadOnlySpan<byte> bgra, int width, byte threshold)
    {
        private readonly ReadOnlySpan<byte> _bgra = bgra;

        public bool IsBlack(int x, int y)
        {
            var o = ((y * width) + x) * Frame.BytesPerPixel;
            return _bgra[o] < threshold && _bgra[o + 1] < threshold && _bgra[o + 2] < threshold;
        }
    }
}
