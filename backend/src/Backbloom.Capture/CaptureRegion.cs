namespace Backbloom.Capture;

/// <summary>
/// The part of a screen that is captured and how it is scaled down: the source rectangle after cropping, the whole
/// scale factor and the size of the delivered image (every <see cref="Divide"/> x <see cref="Divide"/> block of source
/// pixels becomes one output pixel).
/// </summary>
public readonly record struct CaptureRegion(int X, int Y, int Width, int Height, int Divide, int TargetWidth, int TargetHeight)
{
    /// <summary>Crops narrower or lower than this are ignored (the whole screen is used), as in HyperHDR.</summary>
    public const int MinimumCroppedSize = 16;

    /// <summary>
    /// Port of Grabber::getTargetSystemFrameDimension: the smallest whole factor that makes the cropped width fit in
    /// <paramref name="maxWidth"/>. Unlike the C++ code, the crop is always in screen pixels (the GPU path of the C++
    /// grabber applied it to the scaled-down image) and an ignored crop also resets the origin.
    /// </summary>
    public static CaptureRegion Compute(int sourceWidth, int sourceHeight, CropMargins crop, int maxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        maxWidth = Math.Max(1, maxWidth);

        int x = crop.Left, y = crop.Top;
        var width = sourceWidth - crop.Left - crop.Right;
        var height = sourceHeight - crop.Top - crop.Bottom;
        if (width <= MinimumCroppedSize || height <= MinimumCroppedSize)
        {
            (x, y, width, height) = (0, 0, sourceWidth, sourceHeight);
        }

        var divide = 1;
        while (width / divide > maxWidth)
        {
            divide++;
        }

        return new CaptureRegion(x, y, width, height, divide, Math.Max(1, width / divide), Math.Max(1, height / divide));
    }
}
