using HidSharp;

namespace Overglow.Devices.Transport;

/// <summary>An opened HID interface that moves raw reports (hidapi conventions: devices without report IDs use ID 0).</summary>
public interface IHidDevice : IDisposable
{
    /// <summary>Sends an output report: the report ID byte followed by the data. False when the write failed.</summary>
    bool Write(ReadOnlySpan<byte> report);

    /// <summary>Reads one input report without its report ID byte; 0 on timeout.</summary>
    int Read(Span<byte> report, TimeSpan timeout);
}

/// <summary>Opens HID interfaces by device path (from <c>Overglow.Discovery.Usb.HidEnumerator</c>).</summary>
public interface IHidOpener
{
    IHidDevice Open(string path);
}

/// <summary>
/// HID access through HidSharp (Apache-2.0): a managed wrapper over hid.dll/SetupAPI on Windows, hidraw on Linux and
/// IOKit on macOS, so the HID drivers need no platform project.
/// </summary>
public sealed class HidSharpOpener : IHidOpener
{
    public IHidDevice Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var device = DeviceList.Local.GetHidDevices().FirstOrDefault(d => string.Equals(d.DevicePath, path, StringComparison.OrdinalIgnoreCase)) ??
            throw new IOException($"HID device {path} is not present.");
        return new HidSharpDevice(device.Open(), device.GetMaxInputReportLength());
    }

    private sealed class HidSharpDevice(HidStream stream, int inputReportLength) : IHidDevice
    {
        private readonly byte[] _input = new byte[Math.Max(inputReportLength, 2)];

        public bool Write(ReadOnlySpan<byte> report)
        {
            try
            {
                stream.WriteTimeout = 1000;
                stream.Write(report);
                return true;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                return false;
            }
        }

        public int Read(Span<byte> report, TimeSpan timeout)
        {
            try
            {
                stream.ReadTimeout = Math.Max(1, (int)timeout.TotalMilliseconds);
                var length = stream.Read(_input, 0, _input.Length);
                if (length <= 1)
                {
                    return 0;
                }

                var data = _input.AsSpan(1, Math.Min(length - 1, report.Length));
                data.CopyTo(report);
                return data.Length;
            }
            catch (TimeoutException)
            {
                return 0;
            }
            catch (IOException)
            {
                return -1;
            }
        }

        public void Dispose() => stream.Dispose();
    }
}
