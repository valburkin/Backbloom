using System.Numerics;

namespace Backbloom.Devices;

/// <summary>
/// "Find this LED": every other LED is black while the chosen one shows red, green and blue for 1.5 s each
/// (port of the blink handling in <c>LedDevice::rewriteLEDs</c>, sources/led-drivers/LedDevice.cpp).
/// </summary>
public static class BlinkPattern
{
    public static readonly TimeSpan PhaseDuration = TimeSpan.FromMilliseconds(1500);

    public static TimeSpan TotalDuration => PhaseDuration * 3;

    /// <summary>The blinking LED's color at <paramref name="elapsed"/>, or null once the pattern is over.</summary>
    public static Vector3? ColorAt(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero || elapsed >= TotalDuration)
        {
            return null;
        }

        if (elapsed < PhaseDuration)
        {
            return new Vector3(1, 0, 0);
        }

        return elapsed < PhaseDuration * 2 ? new Vector3(0, 1, 0) : new Vector3(0, 0, 1);
    }

    /// <summary>Overwrites <paramref name="colors"/> with the pattern; returns false (and leaves them) when it is over.</summary>
    public static bool Apply(Span<Vector3> colors, int ledIndex, TimeSpan elapsed)
    {
        var color = ColorAt(elapsed);
        if (color is null || (uint)ledIndex >= (uint)colors.Length)
        {
            return false;
        }

        colors.Clear();
        colors[ledIndex] = color.Value;
        return true;
    }
}
