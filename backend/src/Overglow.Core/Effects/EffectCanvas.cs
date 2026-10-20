using System.Runtime.InteropServices;
using Overglow.Core.Color;
using Overglow.Core.Imaging;

namespace Overglow.Core.Effects;

/// <summary>
/// The picture an effect paints, mapped to the LEDs like a screen frame. Port of the painting parts of
/// <c>HyperImage</c> (sources/hyperimage) and <c>ImageData&lt;ColorRgb&gt;</c> (fastBox, gradient boxes; sources/image),
/// with their integer conversions: coordinates go through <c>uint16_t</c> and are clamped to the picture. Paints into
/// a BGRA <see cref="Frame"/> (alpha 255) so the image-to-LED mapping reads it directly; allocates only on resize.
/// </summary>
public sealed class EffectCanvas
{
    /// <summary>The size every kept effect paints at (Animation_Swirl and the music effects use 80x45).</summary>
    public const int DefaultWidth = 80;

    public const int DefaultHeight = 45;

    private double[] _baseAngles = [];

    public EffectCanvas(int width = DefaultWidth, int height = DefaultHeight)
    {
        Frame = new Frame(width, height);
        Clear();
    }

    public Frame Frame { get; }

    public int Width => Frame.Width;

    public int Height => Frame.Height;

    public void Resize(int width, int height)
    {
        if (width != Width || height != Height)
        {
            Frame.Resize(width, height);
            _baseAngles = [];
            Clear();
        }
    }

    /// <summary>Black (<c>Image::clear</c>).</summary>
    public void Clear() => MemoryMarshal.Cast<byte, uint>(Frame.Pixels).Fill(0xff000000u);

    /// <summary>The pixel at (x, y).</summary>
    public Rgb8 this[int x, int y]
    {
        get
        {
            var o = Offset(x, y);
            var px = Frame.ReadOnlyPixels;
            return new Rgb8(px[o + 2], px[o + 1], px[o]);
        }
    }

    /// <summary>Copies the picture as RGB24 rows (3 bytes per pixel), the C++ <c>Image&lt;ColorRgb&gt;</c> layout.</summary>
    public void CopyRgb(Span<byte> rgb)
    {
        var px = Frame.ReadOnlyPixels;
        for (int i = 0, o = 0; i < Width * Height; i++, o += 4)
        {
            rgb[i * 3] = px[o + 2];
            rgb[(i * 3) + 1] = px[o + 1];
            rgb[(i * 3) + 2] = px[o];
        }
    }

    /// <summary>Fills the rectangle between two corners (inclusive). Port of <c>ImageData::fastBox</c>.</summary>
    public void FastBox(int x1, int y1, int x2, int y2, Rgb8 color)
    {
        var ux1 = (ushort)x1;
        var ux2 = (ushort)x2;
        var uy1 = (ushort)y1;
        var uy2 = (ushort)y2;
        var right = (ushort)(Width - 1);
        var bottom = (ushort)(Height - 1);
        int left = Math.Min(Math.Min(ux1, ux2), right);
        int xEnd = Math.Min(Math.Max(ux1, ux2), right);
        int top = Math.Min(Math.Min(uy1, uy2), bottom);
        int yEnd = Math.Min(Math.Max(uy1, uy2), bottom);
        var pixel = 0xff000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        var pixels = MemoryMarshal.Cast<byte, uint>(Frame.Pixels);
        for (var y = top; y <= yEnd; y++)
        {
            pixels.Slice((y * Width) + left, xEnd - left + 1).Fill(pixel);
        }
    }

    /// <summary>
    /// A box with soft left and right edges: five columns fading in and out by a sixth of the color each. Port of
    /// <c>ImageData::gradientHBox</c>.
    /// </summary>
    public void GradientHBox(int x1, int y1, int x2, int y2, Rgb8 color)
    {
        var ux1 = (ushort)x1;
        var ux2 = (ushort)x2;
        var uy1 = (ushort)y1;
        var uy2 = (ushort)y2;
        int left = Math.Min(Math.Min(ux1, ux2), (ushort)(Width - 1));
        int right = Math.Min(Math.Max(ux1, ux2), (ushort)(Width - 1));
        int top = Math.Min(Math.Min(uy1, uy2), (ushort)(Height - 1));
        int bottom = Math.Min(Math.Max(uy1, uy2), (ushort)(Height - 1));
        if (right - left < 4)
        {
            FastBox(left, top, right, bottom, color);
            return;
        }

        int r = color.R / 6, g = color.G / 6, b = color.B / 6;
        for (var x = left - 4; x <= left + 1; x++)
        {
            if (x >= 0)
            {
                FastBox(x, uy1, x, uy2, new Rgb8((byte)r, (byte)g, (byte)b));
            }

            r = Math.Min(r + (color.R / 6), 255);
            g = Math.Min(g + (color.G / 6), 255);
            b = Math.Min(b + (color.B / 6), 255);
        }

        FastBox(left + 2, top, right - 2, bottom, color);

        for (var x = right - 1; x < right + 4; x++)
        {
            if (x < Width)
            {
                FastBox(x, uy1, x, uy2, new Rgb8((byte)r, (byte)g, (byte)b));
            }

            r = Math.Max(r - (color.R / 6), 0);
            g = Math.Max(g - (color.G / 6), 0);
            b = Math.Max(b - (color.B / 6), 0);
        }
    }

    /// <summary>The vertical counterpart of <see cref="GradientHBox"/> (<c>ImageData::gradientVBox</c>).</summary>
    public void GradientVBox(int x1, int y1, int x2, int y2, Rgb8 color)
    {
        var ux1 = (ushort)x1;
        var ux2 = (ushort)x2;
        var uy1 = (ushort)y1;
        var uy2 = (ushort)y2;
        int left = Math.Min(Math.Min(ux1, ux2), (ushort)(Width - 1));
        int right = Math.Min(Math.Max(ux1, ux2), (ushort)(Width - 1));
        int top = Math.Min(Math.Min(uy1, uy2), (ushort)(Height - 1));
        int bottom = Math.Min(Math.Max(uy1, uy2), (ushort)(Height - 1));
        if (bottom - top < 4)
        {
            FastBox(left, top, right, bottom, color);
            return;
        }

        int r = color.R / 6, g = color.G / 6, b = color.B / 6;
        for (var y = top - 4; y <= top + 1; y++)
        {
            if (y >= 0)
            {
                FastBox(ux1, y, ux2, y, new Rgb8((byte)r, (byte)g, (byte)b));
            }

            r = Math.Min(r + (color.R / 6), 255);
            g = Math.Min(g + (color.G / 6), 255);
            b = Math.Min(b + (color.B / 6), 255);
        }

        FastBox(ux1, top + 2, ux2, bottom - 2, color);

        for (var y = bottom - 1; y < bottom + 4; y++)
        {
            if (y < Height)
            {
                FastBox(ux1, y, ux2, y, new Rgb8((byte)r, (byte)g, (byte)b));
            }

            r = Math.Max(r - (color.R / 6), 0);
            g = Math.Max(g - (color.G / 6), 0);
            b = Math.Max(b - (color.B / 6), 0);
        }
    }

    /// <summary>
    /// A conical gradient around the center, rotated by <paramref name="angle"/> degrees. Port of
    /// <c>HyperImage::conicalFill</c> for opaque stops: <paramref name="stops"/> holds (position 0..255, red, green,
    /// blue) quadruples in ascending position, the first at 0 and the last at 255. The angle of each pixel to the center
    /// is computed once per size (atan2 in float as in C++), so a frame costs one pass of integer and float math.
    /// </summary>
    public void ConicalFill(double angle, ReadOnlySpan<byte> stops)
    {
        var w = Width;
        var h = Height;
        if (_baseAngles.Length != w * h)
        {
            _baseAngles = BaseAngles(w, h);
        }

        var pixels = MemoryMarshal.Cast<byte, uint>(Frame.Pixels);
        for (var i = 0; i < w * h; i++)
        {
            var deltaAngle = (float)angle;
            var baseAngle = _baseAngles[i];
            if (!double.IsNaN(baseAngle))
            {
                deltaAngle = (float)(deltaAngle + baseAngle);
            }

            while (deltaAngle > 360.0)
            {
                deltaAngle = (float)(deltaAngle - 360.0);
            }

            while (deltaAngle < 0.0)
            {
                deltaAngle = (float)(deltaAngle + 360.0);
            }

            var position = Math.Clamp((int)Math.Round(deltaAngle * 255 / 360.0, MidpointRounding.AwayFromZero), 0, 255);
            uint r = 0, g = 0, b = 0;
            for (var s = 4; s < stops.Length; s += 4)
            {
                int last = stops[s - 4];
                int next = stops[s];
                if (last <= position && position <= next)
                {
                    float distance = next - last;
                    var aspect2 = (position - last) / distance;
                    var aspect1 = (float)(1.0 - aspect2);
                    r = Channel((stops[s - 3] * aspect1) + (stops[s + 1] * aspect2));
                    g = Channel((stops[s - 2] * aspect1) + (stops[s + 2] * aspect2));
                    b = Channel((stops[s - 1] * aspect1) + (stops[s + 3] * aspect2));
                    break;
                }
            }

            pixels[i] = 0xff000000u | (r << 16) | (g << 8) | b;
        }
    }

    // atan2(1, 0) - atan2f(dy, dx) in degrees for every pixel; NaN at the center (where C++ skipped it).
    private static double[] BaseAngles(int w, int h)
    {
        var pi = (float)(Math.Atan(1) * 4);
        var angles = new double[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                float dx = x - (w / 2);
                float dy = (h / 2) - y;
                var len = (dx * dx) + (dy * dy);
                if (len > 0.0)
                {
                    var newAngle = Math.Atan2(1.0, 0.0) - MathF.Atan2(dy, dx);
                    angles[(y * w) + x] = newAngle * 360.0 / (2 * pi);
                }
                else
                {
                    angles[(y * w) + x] = double.NaN;
                }
            }
        }

        return angles;
    }

    private static uint Channel(float value)
    {
        var v = (int)value;
        return (uint)(v < 0 ? 0 : v > 255 ? 255 : v);
    }

    private int Offset(int x, int y) => ((y * Width) + x) * Frame.BytesPerPixel;
}
