using System.Numerics;
using Backbloom.Core.Color;

namespace Backbloom.Devices;

/// <summary>
/// Applies a controller's color order to a float color. The C++ processing reordered the float colors for every
/// driver except the RGBW encoder; float drivers without their own ordering (LIFX, Hue) use this to match it.
/// </summary>
public static class FloatColorOrder
{
    public static Vector3 Apply(Vector3 c, ColorOrder order) => order switch
    {
        ColorOrder.Bgr => new Vector3(c.Z, c.Y, c.X),
        ColorOrder.Rbg => new Vector3(c.X, c.Z, c.Y),
        ColorOrder.Grb => new Vector3(c.Y, c.X, c.Z),
        ColorOrder.Gbr => new Vector3(c.Y, c.Z, c.X),
        ColorOrder.Brg => new Vector3(c.Z, c.X, c.Y),
        _ => c,
    };
}
