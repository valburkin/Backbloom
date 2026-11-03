using System.Runtime.Versioning;
using Microsoft.Win32;
using TerraFX.Interop.Windows;
using Backbloom.Engine.Platform;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Platform.Windows;

/// <summary>
/// The active monitors from the display configuration API (<c>QueryDisplayConfig</c>, the target's EDID name and
/// device path, the source's GDI name and position) and their physical size from the EDID Windows keeps in the
/// registry (<c>HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\&lt;id&gt;\&lt;instance&gt;\Device Parameters\EDID</c>,
/// opened read-only). Nothing is changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsDisplayList : IDisplayList
{
    private const int Success = 0;
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var result = new List<DisplayInfo>();
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
            if (DisplayConfigGetDeviceInfo(&source.header) != Success || DisplayConfigGetDeviceInfo(&target.header) != Success)
            {
                continue;
            }

            var gdi = new string(&source.viewGdiDeviceName.e0);
            var friendly = new string(&target.monitorFriendlyDeviceName.e0);
            var devicePath = new string(&target.monitorDevicePath.e0);
            var index = path.sourceInfo.modeInfoIdx;
            var primary = index < modeCount && modes[index].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE &&
                modes[index].sourceMode.position.x == 0 && modes[index].sourceMode.position.y == 0;
            var (width, height) = ReadEdid(devicePath) is { } edid ? Edid.PhysicalSize(edid) : (null, null);

            // the EDID has the panel's size in its native (landscape) orientation; a monitor turned to portrait swaps them
            (width, height) = Oriented(width, height, path.targetInfo.rotation);
            result.Add(new DisplayInfo(friendly.Length > 0 ? friendly : gdi, gdi, devicePath, primary, width, height));
        }

        return result;
    }

    /// <summary>
    /// "\\?\DISPLAY#DEL41A4#5&amp;1b6b3b4&amp;0&amp;UID4353#{e6f07b5f-...}" -> "DEL41A4\5&amp;1b6b3b4&amp;0&amp;UID4353", the
    /// monitor's key below <c>Enum\DISPLAY</c>; null when the path has another shape.
    /// </summary>
    public static string? RegistryKeyOf(string devicePath)
    {
        ArgumentNullException.ThrowIfNull(devicePath);
        var parts = devicePath.Split('#');
        return parts.Length >= 3 && parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) && parts[1].Length > 0 && parts[2].Length > 0
            ? $@"{parts[1]}\{parts[2]}"
            : null;
    }

    /// <summary>The size as the desktop shows the monitor: swapped when it is turned a quarter (portrait).</summary>
    internal static (int? Width, int? Height) Oriented(int? width, int? height, DISPLAYCONFIG_ROTATION rotation) =>
        rotation is DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE90 or DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_ROTATE270
            ? (height, width)
            : (width, height);

    private static byte[]? ReadEdid(string devicePath)
    {
        if (RegistryKeyOf(devicePath) is not { } key)
        {
            return null;
        }

        try
        {
            using var parameters = Registry.LocalMachine.OpenSubKey($@"{EnumKey}\{key}\Device Parameters", writable: false);
            return parameters?.GetValue("EDID") as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
