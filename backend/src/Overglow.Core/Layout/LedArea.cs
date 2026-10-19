namespace Overglow.Core.Layout;

/// <summary>
/// The part of the frame one LED samples, as fractions of the frame (0..1), plus its group and whether it is
/// switched off. Port of HyperHDR's <c>LedString::Led</c> (sources/base/LedString.cpp).
/// </summary>
/// <param name="HMin">Left edge (fraction of the frame width).</param>
/// <param name="HMax">Right edge (fraction of the frame width).</param>
/// <param name="VMin">Top edge (fraction of the frame height).</param>
/// <param name="VMax">Bottom edge (fraction of the frame height).</param>
/// <param name="Group">LEDs with the same group &gt; 0 share one averaged color.</param>
/// <param name="Disabled">A disabled LED samples nothing and stays black (unless its group gives it a color).</param>
public readonly record struct LedArea(double HMin, double HMax, double VMin, double VMax, int Group = 0, bool Disabled = false)
{
    /// <summary>True when the area is too thin to sample anything (as in ImageColorAveraging's 1e-6 check).</summary>
    public bool IsEmpty => HMax - HMin < 1e-6 || VMax - VMin < 1e-6;

    /// <summary>Clamps every edge to 0..1 and swaps min/max when they were given the wrong way round.</summary>
    public LedArea Normalized()
    {
        var hMin = Math.Clamp(HMin, 0.0, 1.0);
        var hMax = Math.Clamp(HMax, 0.0, 1.0);
        var vMin = Math.Clamp(VMin, 0.0, 1.0);
        var vMax = Math.Clamp(VMax, 0.0, 1.0);
        if (hMin > hMax)
        {
            (hMin, hMax) = (hMax, hMin);
        }

        if (vMin > vMax)
        {
            (vMin, vMax) = (vMax, vMin);
        }

        return this with { HMin = hMin, HMax = hMax, VMin = vMin, VMax = vMax };
    }
}
