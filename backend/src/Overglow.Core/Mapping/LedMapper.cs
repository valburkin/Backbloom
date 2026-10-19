using System.Numerics;
using Overglow.Core.Imaging;
using Overglow.Core.Layout;

namespace Overglow.Core.Mapping;

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
    public void Process(Frame frame, LedMappingOptions options, Span<Vector3> ledColors)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        var map = _map;
        if (map is null || map.Width != frame.Width || map.Height != frame.Height || !ReferenceEquals(map.Layout, options.Layout) ||
            map.MappingType != options.MappingType || map.SparseProcessing != options.SparseProcessing)
        {
            map = new ImageToLedMap(options.Layout, options.MappingType, options.SparseProcessing, frame.Width, frame.Height);
            _map = map;
            MapRebuilt?.Invoke(map);
        }

        map.Process(frame.ReadOnlyPixels, ledColors);
    }
}
