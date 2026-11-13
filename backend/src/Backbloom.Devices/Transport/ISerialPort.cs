using System.Diagnostics.CodeAnalysis;
using System.IO.Ports;

namespace Backbloom.Devices.Transport;

/// <summary>A serial (UART / USB CDC) port. Abstracted so the serial drivers can be tested against a fake.</summary>
public interface ISerialPort : IDisposable
{
    string PortName { get; }

    bool IsOpen { get; }

    bool DtrEnable { get; set; }

    bool RtsEnable { get; set; }

    /// <summary>Bytes received and not read yet.</summary>
    int BytesToRead { get; }

    void Open();

    /// <summary>Writes all bytes; throws <see cref="TimeoutException"/> or <see cref="IOException"/> when that fails.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Reads what is available (at most the buffer), waiting up to <paramref name="timeout"/> for the first byte; 0 on timeout.</summary>
    int Read(Span<byte> buffer, TimeSpan timeout);

    /// <summary>Waits until the written bytes left the driver's buffer.</summary>
    void Flush();

    void Close();
}

/// <summary>Opens serial ports by name ("COM3", "/dev/ttyUSB0").</summary>
public interface ISerialPortFactory
{
    ISerialPort Create(string portName, int baudRate);
}

/// <summary><see cref="ISerialPort"/> over System.IO.Ports.</summary>
[ExcludeFromCodeCoverage(Justification = "Opens real serial ports; tests never touch hardware. The serial drivers are tested against a fake ISerialPort.")]
public sealed class SystemSerialPort(string portName, int baudRate) : ISerialPort
{
    private static readonly TimeSpan s_writeTimeout = TimeSpan.FromSeconds(1);

    private readonly SerialPort _port = new(portName, baudRate)
    {
        WriteTimeout = (int)s_writeTimeout.TotalMilliseconds,
        ReadTimeout = 100,
        Handshake = Handshake.None,
    };

    public string PortName => _port.PortName;

    public bool IsOpen => _port.IsOpen;

    public bool DtrEnable
    {
        get => _port.DtrEnable;
        set => _port.DtrEnable = value;
    }

    public bool RtsEnable
    {
        get => _port.RtsEnable;
        set => _port.RtsEnable = value;
    }

    public int BytesToRead => _port.IsOpen ? _port.BytesToRead : 0;

    public void Open() => _port.Open();

    public void Write(ReadOnlySpan<byte> data) => _port.BaseStream.Write(data);

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        _port.ReadTimeout = Math.Max(1, (int)timeout.TotalMilliseconds);
        try
        {
            return _port.BaseStream.Read(buffer);
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void Flush() => _port.BaseStream.Flush();

    public void Close() => _port.Close();

    public void Dispose() => _port.Dispose();
}

public sealed class SystemSerialPortFactory : ISerialPortFactory
{
    public ISerialPort Create(string portName, int baudRate) => new SystemSerialPort(portName, baudRate);
}
