using System.Numerics;
using Backbloom.Core.Layout;

namespace Backbloom.Core.Smoothing;

/// <summary>
/// Slows down the LEDs that show the average of the whole screen ("Screen average" parts), on top of the smoothing
/// every LED gets: an exponential filter on their linear color, before the color settings.
/// </summary>
/// <remarks>
/// <para>A whole-screen average is a pale color whose hue swings with small changes in the picture (an olive moves
/// between yellow and green when red changes by a few levels), and the saturation gain makes the swings plain to see.
/// The filter keeps them out while the screen edges stay as fast as the smoothing settings say.</para>
/// <para>Follows the time, not the frame count, so it behaves the same at any capture rate; after a pause longer than a
/// few time constants (another source showed) it starts at the current color. Does not allocate; not thread-safe.</para>
/// </remarks>
public sealed class ScreenAverageFilter
{
    /// <summary>The filter's time constant: after it, 63% of a change has come through.</summary>
    public const float TimeConstantMs = 500.0f;

    private Vector3 _value;
    private long _lastMs;
    private bool _hasValue;

    /// <summary>Filters the whole-screen LEDs of <paramref name="layout"/> in <paramref name="colors"/> (linear light).</summary>
    public void Apply(LedLayout layout, Span<Vector3> colors, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var leds = layout.WholeScreenLeds;
        if (leds.IsEmpty || colors.Length < layout.Count)
        {
            _hasValue = false;
            return;
        }

        // every whole-screen LED holds the same average
        var target = colors[leds[0]];
        var elapsed = nowMs - _lastMs;
        if (!_hasValue || elapsed < 0)
        {
            _value = target;
        }
        else
        {
            var alpha = 1.0f - MathF.Exp(-elapsed / TimeConstantMs);
            _value += (target - _value) * alpha;
        }

        _lastMs = nowMs;
        _hasValue = true;
        foreach (var led in leds)
        {
            colors[led] = _value;
        }
    }
}
