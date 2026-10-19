using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Overglow.Core.Layout;

/// <summary>
/// The ordered list of LED sampling areas (the legacy HyperHDR <c>leds</c> setting). Immutable; build a new
/// instance when the setting changes. Port of <c>LedString</c> (sources/base/LedString.cpp).
/// </summary>
public sealed class LedLayout
{
    private const int MaxGridSize = 80;

    /// <summary>A layout without LEDs.</summary>
    public static LedLayout Empty { get; } = new([]);

    /// <summary>Creates a layout; every area is normalized (clamped to 0..1, min/max swapped when reversed).</summary>
    public LedLayout(IEnumerable<LedArea> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        Areas = [.. areas.Select(a => a.Normalized())];
        HasDisabled = Areas.Any(a => a.Disabled);
    }

    public ImmutableArray<LedArea> Areas { get; }

    public int Count => Areas.Length;

    public bool HasDisabled { get; }

    /// <summary>
    /// Parses the <c>leds</c> setting: an array of <c>{hmin, hmax, vmin, vmax, group?, disabled?}</c>.
    /// Like <c>LedString::createLedString</c>, missing numbers read as 0, a missing group as 0 and a missing
    /// <c>disabled</c> as false; non-object entries become empty areas so LED indexes stay aligned.
    /// </summary>
    public static LedLayout FromJson(JsonNode? leds)
    {
        if (leds is not JsonArray array)
        {
            return Empty;
        }

        var areas = new List<LedArea>(array.Count);
        foreach (var item in array)
        {
            var o = item as JsonObject;
            areas.Add(new LedArea(
                ReadDouble(o, "hmin"),
                ReadDouble(o, "hmax"),
                ReadDouble(o, "vmin"),
                ReadDouble(o, "vmax"),
                (int)ReadDouble(o, "group"),
                ReadBool(o, "disabled")));
        }

        return new LedLayout(areas);
    }

    /// <summary>
    /// Size of a coarse grid that has one cell per distinct LED center column/row, corrected to a sane aspect ratio
    /// and limited to 80x80. Port of <c>LedString::getLedLayoutGridSize</c>.
    /// </summary>
    public (int Width, int Height) GetGridSize()
    {
        var xs = new SortedSet<int>();
        var ys = new SortedSet<int>();
        foreach (var a in Areas)
        {
            xs.Add((int)(1000.0 * (a.HMin + a.HMax) / 2.0));
            ys.Add((int)(1000.0 * (a.VMin + a.VMax) / 2.0));
        }

        var width = xs.Count;
        var height = ys.Count;

        // Expected is at least 50% of width <-> height.
        if (width > 2 * height)
        {
            height = Math.Max(1, width / 2);
        }
        else if (width < 0.5 * height)
        {
            width = Math.Max(1, height / 2);
        }

        if (width > MaxGridSize || height > MaxGridSize)
        {
            (width, height) = ScaleKeepAspectRatio(width, height, MaxGridSize, MaxGridSize);
        }

        return (width, height);
    }

    // Same integer math as QSize::scaled(..., Qt::KeepAspectRatio).
    private static (int Width, int Height) ScaleKeepAspectRatio(int width, int height, int targetWidth, int targetHeight)
    {
        if (width == 0 || height == 0)
        {
            return (targetWidth, targetHeight);
        }

        var rw = (long)targetHeight * width / height;
        if (rw <= targetWidth)
        {
            return ((int)rw, targetHeight);
        }

        var rh = (long)targetWidth * height / width;
        return (targetWidth, (int)rh);
    }

    private static double ReadDouble(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue(out double d) ? d : 0.0;

    private static bool ReadBool(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue(out bool b) && b;
}
