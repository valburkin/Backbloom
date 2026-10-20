using System.Numerics;
using Backbloom.Core.Borders;
using Backbloom.Core.Imaging;
using Backbloom.Core.Layout;

namespace Backbloom.Core.Mapping;

/// <summary>What the mapper needs from the settings: the LED layout and how to sample it.</summary>
public sealed record LedMappingOptions(LedLayout Layout, LedMappingType MappingType = LedMappingType.Advanced, bool SparseProcessing = false);

/// <summary>
/// Keeps an <see cref="ImageToLedMap"/> matching the current frame size and options, rebuilding it only when either
/// changes. Port of the mapping part of <c>ImageToLedManager</c> (sources/base/ImageToLedManager.cpp).
/// </summary>
/// <remarks>Meant for the single frame-loop thread; allocates only when it has to rebuild the map.</remarks>
public sealed class LedMapper
{
    private ImageToLedMap? _map;

    /// <summary>Raised on the calling thread after the map was rebuilt.</summary>
    public event Action<ImageToLedMap>? MapRebuilt;

    /// <summary>Averages <paramref name="frame"/> into <paramref name="ledColors"/> (one linear color per LED).</summary>
    /// <param name="border">
    /// The black border to leave out (the LED areas are spread over the rest of the picture); unknown means none, and
    /// so does a border that leaves no picture.
    /// </param>
    public void Process(Frame frame, LedMappingOptions options, Span<Vector3> ledColors, BlackBorder border = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        var (horizontal, vertical) = border.IsUnknown || 2 * border.HorizontalSize >= frame.Height || 2 * border.VerticalSize >= frame.Width
            ? (0, 0)
            : (Math.Max(border.HorizontalSize, 0), Math.Max(border.VerticalSize, 0));
        var map = _map;
        if (map is null || map.Width != frame.Width || map.Height != frame.Height || !ReferenceEquals(map.Layout, options.Layout) ||
            map.MappingType != options.MappingType || map.SparseProcessing != options.SparseProcessing ||
            map.HorizontalBorder != horizontal || map.VerticalBorder != vertical)
        {
            map = new ImageToLedMap(options.Layout, options.MappingType, options.SparseProcessing, frame.Width, frame.Height, horizontal, vertical);
            _map = map;
            MapRebuilt?.Invoke(map);
        }

        map.Process(frame.ReadOnlyPixels, ledColors);
    }
}
