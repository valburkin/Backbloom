using System.Buffers.Binary;
using Backbloom.Capture.Hdr;
using Backbloom.Core.Imaging;

namespace Backbloom.Capture;

/// <summary>
/// The CPU capture path: scales a full-size BGRA screen image down by taking every <see cref="CaptureRegion.Divide"/>-th
/// pixel of the cropped area, optionally through the tone mapping table. Port of decodeBGRA in DxGrabber.cpp (which
/// wrote RGB24; this writes BGRA frames). Doesn't allocate.
/// </summary>
public static class BgraDownscaler
{
    /// <param name="source">Top-left of the screen image.</param>
    /// <param name="rowPitch">Bytes per source row (at least width * 4; GPU readbacks are padded).</param>
    /// <param name="sourceHeight">Rows in <paramref name="source"/>; reads are clamped to it.</param>
    /// <param name="region">Crop and scale; <paramref name="target"/> must be at least its target size.</param>
    /// <param name="lut">Tone mapping table, or null to copy the colors.</param>
    /// <param name="target">Frame to write.</param>
    /// <param name="targetX">Column of <paramref name="target"/> where the image starts (multi-monitor layouts).</param>
    public static void Downscale(
        ReadOnlySpan<byte> source, int rowPitch, int sourceHeight, CaptureRegion region, ToneMappingLut? lut, Frame target, int targetX = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (targetX < 0 || targetX + region.TargetWidth > target.Width || region.TargetHeight > target.Height)
        {
            throw new ArgumentException("The target frame is smaller than the capture region.", nameof(target));
        }

        var step = region.Divide * Frame.BytesPerPixel;
        var table = lut is null ? default : lut.Table;
        var pixels = target.Pixels;
        for (var j = 0; j < region.TargetHeight; j++)
        {
            var line = Math.Min(region.Y + (j * region.Divide), sourceHeight - 1);
            var s = source[((line * rowPitch) + (region.X * Frame.BytesPerPixel))..];
            var d = pixels.Slice((j * target.Stride) + (targetX * Frame.BytesPerPixel), region.TargetWidth * Frame.BytesPerPixel);
            if (table.IsEmpty)
            {
                for (int i = 0, o = 0; o < d.Length; i += step, o += Frame.BytesPerPixel)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(d[o..], BinaryPrimitives.ReadUInt32LittleEndian(s[i..]) | 0xFF000000u);
                }
            }
            else
            {
                for (int i = 0, o = 0; o < d.Length; i += step, o += Frame.BytesPerPixel)
                {
                    var t = ToneMappingLut.Index(s[i + 2], s[i + 1], s[i]);
                    d[o] = table[t + 2];
                    d[o + 1] = table[t + 1];
                    d[o + 2] = table[t];
                    d[o + 3] = 255;
                }
            }
        }
    }
}
