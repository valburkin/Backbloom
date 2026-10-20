using HidSharp;

namespace Overglow.Discovery.Usb;

/// <summary>One HID interface (top-level collection) present in the system.</summary>
/// <param name="Path">Device path, used to open it.</param>
/// <param name="UsagePage">Usage page of the top-level collection (0 when unknown); a device may expose several interfaces.</param>
public sealed record HidDeviceInfo(string Path, string Manufacturer, string Product, string Serial, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage);

public interface IHidEnumerator
{
    IReadOnlyList<HidDeviceInfo> GetDevices();
}

/// <summary>
/// Lists HID devices with HidSharp (port of <c>ProviderHid::enumerateDevices</c>, which used SetupAPI and hid.dll).
/// Devices whose details can't be read (e.g. opened exclusively) are skipped.
/// </summary>
public sealed class HidEnumerator : IHidEnumerator
{
    public IReadOnlyList<HidDeviceInfo> GetDevices()
    {
        var devices = new List<HidDeviceInfo>();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            try
            {
                var (page, usage) = TopLevelUsage(device);
                devices.Add(new HidDeviceInfo(
                    device.DevicePath,
                    Text(device.GetManufacturer),
                    Text(device.GetProductName),
                    Text(device.GetSerialNumber),
                    (ushort)device.VendorID,
                    (ushort)device.ProductID,
                    page,
                    usage));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        return devices;
    }

    private static (ushort Page, ushort Usage) TopLevelUsage(HidDevice device)
    {
        try
        {
            var items = device.GetReportDescriptor().DeviceItems;
            var value = items.Count > 0 ? items[0].Usages.GetAllValues().FirstOrDefault() : 0u;
            return ((ushort)(value >> 16), (ushort)(value & 0xffff));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return (0, 0);
        }
    }

    private static string Text(Func<string> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return string.Empty;
        }
    }
}
