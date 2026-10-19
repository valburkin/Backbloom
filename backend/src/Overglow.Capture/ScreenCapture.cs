namespace Overglow.Capture;

/// <summary>A monitor that can be captured.</summary>
/// <param name="Id">
/// Setting value that selects it: <c>\\.\DISPLAY5|NVIDIA GeForce RTX 5080</c> (GDI name and adapter, as HyperHDR
/// stored it). GDI numbers can change when monitors are reconnected; <paramref name="DevicePath"/> doesn't.
/// </param>
/// <param name="DevicePath">
/// Stable monitor id from the display configuration: the monitor device path (<c>\\?\DISPLAY#GSM5BBF#...</c>) or, when the
/// driver reports none, <c>edid:manufacturer:product:connector</c>. Also accepted as setting value.
/// </param>
/// <param name="FriendlyName">Monitor name from its EDID (e.g. "LG ULTRAGEAR"), or the GDI name when unknown.</param>
/// <param name="Adapter">Graphics adapter driving it.</param>
/// <param name="Width">Desktop width in pixels.</param>
/// <param name="Height">Desktop height in pixels.</param>
/// <param name="Hdr">True when Windows HDR is on for this monitor (PQ / BT.2020 output).</param>
public sealed record MonitorInfo(string Id, string DevicePath, string FriendlyName, string Adapter, int Width, int Height, bool Hdr)
{
    /// <summary>True when <paramref name="device"/> (a setting value) names this monitor.</summary>
    public bool Matches(string device) =>
        string.Equals(device, Id, StringComparison.OrdinalIgnoreCase) ||
        (DevicePath.Length > 0 && string.Equals(device, DevicePath, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A frame source that captures monitors and is reconfigured from the <c>systemGrabber</c> settings.</summary>
public interface IScreenCaptureSource : IFrameSource
{
    /// <summary>Monitors found by the last enumeration.</summary>
    IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Applies new settings from the next captured frame on (thread-safe).</summary>
    void Configure(CaptureSettings settings);
}

/// <summary>Order of the monitors in a multi-monitor capture.</summary>
public static class DisplayOrder
{
    /// <summary>
    /// Port of the <c>reorder_displays</c> loop in DxGrabber::grabFrame: starting from left to right, advance to the next
    /// lexicographic permutation <paramref name="permutation"/> times, stopping (back at left to right) after the last one.
    /// Returns, for each position, the index of the monitor shown there.
    /// </summary>
    public static int[] Permute(int count, int permutation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var order = Enumerable.Range(0, count).ToArray();
        var done = 0;
        while (done < permutation && NextPermutation(order))
        {
            done++;
        }

        return order;
    }

    /// <summary>std::next_permutation: false (and sorted) after the last permutation.</summary>
    private static bool NextPermutation(int[] a)
    {
        var i = a.Length - 2;
        while (i >= 0 && a[i] >= a[i + 1])
        {
            i--;
        }

        if (i < 0)
        {
            Array.Reverse(a);
            return false;
        }

        var j = a.Length - 1;
        while (a[j] <= a[i])
        {
            j--;
        }

        (a[i], a[j]) = (a[j], a[i]);
        Array.Reverse(a, i + 1, a.Length - i - 1);
        return true;
    }
}
