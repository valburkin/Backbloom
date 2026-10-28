using System.Runtime.Versioning;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Capture.Windows;

/// <summary>A monitor and where DXGI finds it (adapter and output index at enumeration time).</summary>
internal readonly record struct OutputLocation(uint Adapter, uint Output, MonitorInfo Info, bool Primary);

/// <summary>How <see cref="MonitorEnumerator.SelectFor"/> found the monitors.</summary>
internal enum MonitorMatch
{
    /// <summary>As the setting says (or nothing found).</summary>
    Setting,

    /// <summary>The monitor the setting captured before, under a new GDI name.</summary>
    Renamed,

    /// <summary>The setting names no connected monitor; the only connected one is used.</summary>
    OnlyMonitor,
}

/// <summary>
/// Lists the monitors attached to the desktop (port of DxGrabber::enumerateDevices), with the EDID name and the
/// stable device path from the display configuration API.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static unsafe class MonitorEnumerator
{
    /// <summary>Prefix of the setting value that captures every monitor of one adapter side by side.</summary>
    public const string MultiMonitorPrefix = "MULTI-MONITOR|";

    private const int Success = 0;

    public static List<OutputLocation> Enumerate()
    {
        // Physical desktop coordinates, not the ones scaled for DPI-unaware threads.
        var previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            return EnumerateOutputs();
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private static List<OutputLocation> EnumerateOutputs()
    {
        var names = ReadDisplayConfigNames();
        var result = new List<OutputLocation>();
        IDXGIFactory1* factory = null;
        Com.Check(CreateDXGIFactory1(__uuidof<IDXGIFactory1>(), (void**)&factory), "CreateDXGIFactory1");
        try
        {
            IDXGIAdapter1* adapter = null;
            for (uint a = 0; factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; a++)
            {
                try
                {
                    DXGI_ADAPTER_DESC1 adapterDesc;
                    Com.Check(adapter->GetDesc1(&adapterDesc), "IDXGIAdapter1::GetDesc1");
                    var adapterName = new string(&adapterDesc.Description.e0);
                    IDXGIOutput* output = null;
                    for (uint o = 0; adapter->EnumOutputs(o, &output) != DXGI_ERROR_NOT_FOUND; o++)
                    {
                        try
                        {
                            if (Describe(output, adapterName, names) is { } info)
                            {
                                result.Add(new OutputLocation(a, o, info.Monitor, info.Primary));
                            }
                        }
                        finally
                        {
                            Com.Release(ref output);
                        }
                    }
                }
                finally
                {
                    Com.Release(ref adapter);
                }
            }
        }
        finally
        {
            Com.Release(ref factory);
        }

        // Sorted like HyperHDR's device list.
        result.Sort((x, y) => string.CompareOrdinal(x.Info.Id, y.Info.Id));
        return result;
    }

    /// <summary>
    /// Picks the outputs for a <c>device</c> setting: "auto" is the primary monitor (HyperHDR took the first in sorted
    /// order, which could be a multi-monitor entry), a monitor id or device path selects one monitor, and
    /// "MULTI-MONITOR|adapter" every monitor of that adapter. Empty when nothing matches.
    /// </summary>
    public static List<OutputLocation> Select(IReadOnlyList<OutputLocation> outputs, string device)
    {
        if (string.Equals(device, CaptureSettings.AutoDevice, StringComparison.OrdinalIgnoreCase))
        {
            var primary = outputs.FirstOrDefault(o => o.Primary);
            return outputs.Count == 0 ? [] : [primary.Info is null ? outputs[0] : primary];
        }

        if (device.StartsWith(MultiMonitorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var adapter = device[MultiMonitorPrefix.Length..];
            return [.. outputs.Where(o => string.Equals(o.Info.Adapter, adapter, StringComparison.OrdinalIgnoreCase))];
        }

        return [.. outputs.Where(o => o.Info.Matches(device)).Take(1)];
    }

    /// <summary>
    /// <see cref="Select"/>, except that a setting naming one monitor still finds it when Windows gave it another GDI
    /// name (it renumbers them after a driver update or a reconnect, and HyperHDR's ids hold that name): first by
    /// <paramref name="lastPath"/>, the device path of the monitor this setting captured before (also when another
    /// monitor took over the old name). When nothing matches and only one monitor is connected, that one, but only
    /// while the setting's monitor isn't known yet: a known one that is away (a TV switched off) is waited for rather
    /// than replaced by the remaining screen.
    /// </summary>
    public static List<OutputLocation> SelectFor(IReadOnlyList<OutputLocation> outputs, string device, string? lastPath, out MonitorMatch match)
    {
        match = MonitorMatch.Setting;
        var single = !string.Equals(device, CaptureSettings.AutoDevice, StringComparison.OrdinalIgnoreCase) &&
            !device.StartsWith(MultiMonitorPrefix, StringComparison.OrdinalIgnoreCase);
        var known = single && !string.IsNullOrEmpty(lastPath);
        if (known)
        {
            foreach (var o in outputs)
            {
                if (string.Equals(o.Info.DevicePath, lastPath, StringComparison.OrdinalIgnoreCase))
                {
                    match = o.Info.Matches(device) ? MonitorMatch.Setting : MonitorMatch.Renamed;
                    return [o];
                }
            }
        }

        var selected = Select(outputs, device);
        if (selected.Count == 0 && single && !known && outputs.Count == 1)
        {
            match = MonitorMatch.OnlyMonitor;
            return [outputs[0]];
        }

        return selected;
    }

    /// <summary>
    /// The device path <see cref="SelectFor"/> is to get as <c>lastPath</c> next time: the one monitor the setting
    /// selected itself or under a new name, not one only taken because it was the only one (null otherwise).
    /// </summary>
    public static string? PathToRemember(List<OutputLocation> selected, MonitorMatch match) =>
        match != MonitorMatch.OnlyMonitor && selected is [var one] && one.Info.DevicePath.Length > 0 ? one.Info.DevicePath : null;

    /// <summary>Multi-monitor ids for adapters with more than one monitor.</summary>
    public static IEnumerable<string> MultiMonitorIds(IReadOnlyList<OutputLocation> outputs) =>
        outputs.GroupBy(o => o.Info.Adapter, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => MultiMonitorPrefix + g.Key);

    private static (MonitorInfo Monitor, bool Primary)? Describe(IDXGIOutput* output, string adapterName, Dictionary<string, (string Friendly, string Path)> names)
    {
        IDXGIOutput6* output6 = null;
        if (output->QueryInterface(__uuidof<IDXGIOutput6>(), (void**)&output6).FAILED)
        {
            return null;
        }

        try
        {
            DXGI_OUTPUT_DESC1 desc;
            Com.Check(output6->GetDesc1(&desc), "IDXGIOutput6::GetDesc1");
            if (desc.AttachedToDesktop == 0)
            {
                return null;
            }

            var gdiName = new string(&desc.DeviceName.e0);
            var (friendly, path) = names.TryGetValue(gdiName, out var n) ? n : (gdiName, string.Empty);
            var r = desc.DesktopCoordinates;
            var hdr = desc.ColorSpace == DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
            var monitor = new MonitorInfo($"{gdiName}|{adapterName}", path, friendly, adapterName, r.right - r.left, r.bottom - r.top, hdr);
            return (monitor, r.left == 0 && r.top == 0);
        }
        finally
        {
            Com.Release(ref output6);
        }
    }

    /// <summary>GDI device name (\\.\DISPLAY1) to the monitor's EDID name and device path.</summary>
    private static Dictionary<string, (string Friendly, string Path)> ReadDisplayConfigNames()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        uint pathCount, modeCount;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != Success)
        {
            return result;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, p, &modeCount, m, null) != Success)
            {
                return result;
            }
        }

        foreach (var path in paths.AsSpan(0, (int)pathCount))
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;
            if (DisplayConfigGetDeviceInfo(&source.header) == Success &&
                DisplayConfigGetDeviceInfo(&target.header) == Success)
            {
                var gdi = new string(&source.viewGdiDeviceName.e0);
                var friendly = new string(&target.monitorFriendlyDeviceName.e0);
                var devicePath = new string(&target.monitorDevicePath.e0);
                if (devicePath.Length == 0)
                {
                    // Some drivers report no path: the EDID ids and the connector identify the monitor as well.
                    devicePath = $"edid:{target.edidManufactureId:X4}:{target.edidProductCodeId:X4}:{target.connectorInstance}";
                }

                result[gdi] = (friendly.Length > 0 ? friendly : gdi, devicePath);
            }
        }

        return result;
    }
}
