using System.Numerics;

namespace Backbloom.Devices;

/// <summary>
/// "Find this LED": every other LED is black while the chosen one shows red, green and blue for 1.5 s each
/// (port of the blink handling in <c>LedDevice::rewriteLEDs</c>, sources/led-drivers/LedDevice.cpp). Held (an
/// addition), it keeps cycling red, green, blue until it is stopped, or for at most <see cref="HoldTimeout"/>.
/// </summary>
public static class BlinkPattern
{
    public static readonly TimeSpan PhaseDuration = TimeSpan.FromMilliseconds(1500);

    /// <summary>A held blink ends by itself after this long (a safety net for a client that went away).</summary>
    public static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(120);

    public static TimeSpan TotalDuration => PhaseDuration * 3;

    /// <summary>The blinking LED's color at <paramref name="elapsed"/>, or null once the pattern is over.</summary>
    public static Vector3? ColorAt(TimeSpan elapsed, bool hold = false)
    {
        if (elapsed < TimeSpan.Zero || elapsed >= (hold ? HoldTimeout : TotalDuration))
        {
            return null;
        }

        return (elapsed.Ticks / PhaseDuration.Ticks % 3) switch
        {
            0 => new Vector3(1, 0, 0),
            1 => new Vector3(0, 1, 0),
            _ => new Vector3(0, 0, 1),
        };
    }

    /// <summary>Overwrites <paramref name="colors"/> with the pattern; returns false (and leaves them) when it is over.</summary>
    public static bool Apply(Span<Vector3> colors, int ledIndex, TimeSpan elapsed, bool hold = false)
    {
        var color = ColorAt(elapsed, hold);
        if (color is null || (uint)ledIndex >= (uint)colors.Length)
        {
            return false;
        }

        colors.Clear();
        colors[ledIndex] = color.Value;
        return true;
    }
}
