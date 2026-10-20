using System.Globalization;
using System.IO.Ports;
using System.Text.RegularExpressions;
using HidSharp;

namespace Backbloom.Discovery.Usb;

/// <summary>A serial port present in the system.</summary>
/// <param name="PortName">What the serial drivers open: "COM3", "ttyUSB0".</param>
/// <param name="SystemLocation">"\\.\COM3" or "/dev/ttyUSB0".</param>
/// <param name="Description">Friendly name of the device, when the OS reports one.</param>
/// <param name="VendorId">USB vendor id, when the port belongs to a USB device and it is known.</param>
public sealed record SerialPortInfo(string PortName, string SystemLocation, string Description, ushort? VendorId, ushort? ProductId)
{
    /// <summary>Boards that run the HyperSerial firmwares (ESP32-S2, RP2040/RP2350).</summary>
    public bool IsKnownEspOrPico =>
        (VendorId == 0x303a && ProductId == 0x80c2) || (VendorId == 0x2e8a && ProductId is 0x0a or 0x09);

    /// <summary>USB serial bridges commonly used with ESP boards (Espressif, CP210x, CH340, CH9102).</summary>
    public bool IsLikelyEsp =>
        VendorId == 0x303a || (VendorId == 0x10c4 && ProductId == 0xea60) || (VendorId == 0x1a86 && ProductId is 0x7523 or 0x55d4);
}

public interface ISerialPortEnumerator
{
    IReadOnlyList<SerialPortInfo> GetPorts();
}

/// <summary>
/// Lists serial ports with their friendly names and USB ids (port of the QSerialPortInfo use in ProviderSerial.cpp):
/// HidSharp's serial device list gives the names and the device interface path, which carries "VID_xxxx" and
/// "PID_xxxx" for USB devices on Windows; ports only System.IO.Ports knows are added without details.
/// </summary>
public sealed partial class SerialPortEnumerator : ISerialPortEnumerator
{
    public IReadOnlyList<SerialPortInfo> GetPorts()
    {
        var ports = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in DeviceList.Local.GetSerialDevices())
        {
            string location;
            try
            {
                location = device.GetFileSystemName();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }

            var name = PortNameOf(location);
            var (vendor, product) = UsbIds(device.DevicePath);
            ports[name] = new SerialPortInfo(name, SystemLocation(name, location), Description(FriendlyName(device), name), vendor, product);
        }

        foreach (var name in SerialPort.GetPortNames())
        {
            var portName = PortNameOf(name);
            if (!ports.ContainsKey(portName))
            {
                ports[portName] = new SerialPortInfo(portName, SystemLocation(portName, name), string.Empty, null, null);
            }
        }

        return [.. ports.Values.OrderBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"\\.\COM3" -> "COM3", "/dev/ttyUSB0" -> "ttyUSB0".</summary>
    public static string PortNameOf(string location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var slash = location.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? location[(slash + 1)..] : location;
    }

    /// <summary>Where the port lives, as QSerialPortInfo::systemLocation reports it ("\\.\COM3" on Windows).</summary>
    public static string SystemLocation(string portName, string location) =>
        OperatingSystem.IsWindows() && !location.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\.\" + portName : location;

    /// <summary>The friendly name without the " (COM3)" Windows appends to it.</summary>
    public static string Description(string friendlyName, string portName)
    {
        ArgumentNullException.ThrowIfNull(friendlyName);
        var suffix = $" ({portName})";
        return friendlyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? friendlyName[..^suffix.Length] : friendlyName;
    }

    /// <summary>USB vendor and product id from a Windows device path ("...VID_1A86&amp;PID_7523..." or FTDI's "VID_0403+PID_6001").</summary>
    public static (ushort? Vendor, ushort? Product) UsbIds(string devicePath)
    {
        var match = UsbIdPattern().Match(devicePath ?? string.Empty);
        return match.Success
            ? (ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : (null, null);
    }

    private static string FriendlyName(SerialDevice device)
    {
        try
        {
            return device.GetFriendlyName() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex("VID_([0-9A-F]{4})[&+]PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbIdPattern();
}
