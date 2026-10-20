using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Devices.Transport;
using Overglow.Discovery.Usb;

namespace Overglow.Devices.Drivers.Serial;

/// <summary>Settings shared by the serial drivers (output, rate, delayAfterConnect, espHandshake, ...).</summary>
/// <param name="Output">Port name ("COM3", "ttyUSB0") or "auto".</param>
public sealed record SerialOptions(
    string Output = "auto",
    int BaudRate = 2000000,
    int DelayAfterConnectMs = 0,
    bool EspHandshake = false,
    bool ForceSerialDetection = false,
    int MaxRetry = 60)
{
    public bool IsAuto => string.Equals(Output, "auto", StringComparison.OrdinalIgnoreCase);

    public static SerialOptions FromConfig(JsonObject config, int defaultRate)
    {
        ArgumentNullException.ThrowIfNull(config);
        var output = DeviceConfig.GetString(config, "output", "auto").Trim();
        if (output.StartsWith("/dev/", StringComparison.Ordinal))
        {
            output = output[5..];
        }

        return new SerialOptions(
            output.Length == 0 ? "auto" : output,
            DeviceConfig.GetInt(config, "rate", defaultRate),
            DeviceConfig.GetInt(config, "delayAfterConnect", 0),
            DeviceConfig.GetBool(config, "espHandshake", false),
            DeviceConfig.GetBool(config, "forceSerialDetection", false),
            DeviceConfig.GetInt(config, "maxRetry", 60));
    }
}

/// <summary>
/// Base of the serial drivers (port of <c>ProviderSerial</c> and <c>EspTools</c>,
/// sources/led-drivers/serial/ProviderSerial.cpp): picks the port ("auto" prefers known ESP / Pico boards), opens it,
/// runs the HyperSerial handshake when asked and says goodbye on close. A port that fails while streaming (unplugged,
/// more than five write timeouts in a row) is reported to the runner, which reopens it with the retry policy.
/// </summary>
public abstract partial class SerialLedDriver : ILedDriver
{
    /// <summary>HyperSerial "hello" (AWA protocol control frame, "hyperhdr").</summary>
    internal static readonly byte[] EspHello = [0x41, 0x77, 0x41, 0x2a, 0xa2, 0x15, 0x68, 0x79, 0x70, 0x65, 0x72, 0x68, 0x64, 0x72];

    /// <summary>HyperSerial "going to sleep": the firmware answers with its statistics.</summary>
    internal static readonly byte[] EspGoodbye = [0x41, 0x77, 0x41, 0x2a, 0xa2, 0x35, 0x68, 0x79, 0x70, 0x65, 0x72, 0x68, 0x64, 0x72];

    private const int MaxWriteTimeouts = 5;
    private const int BlackFramesOnClose = 3;

    private readonly ISerialPortFactory _factory;
    private readonly ISerialPortEnumerator _enumerator;
    private readonly TimeProvider _time;
    private ISerialPort? _port;
    private int _writeTimeouts;
    private byte[] _black = [];

    protected SerialLedDriver(SerialOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        Options = options;
        _factory = context.SerialPorts;
        _enumerator = context.Discovery.SerialPorts;
        _time = context.Time;
        _logger = context.LoggerFactory.CreateLogger(GetType());
        RetryPolicy = new RetryPolicy(Math.Max(0, options.MaxRetry), TimeSpan.FromMilliseconds(2500));
    }

    public abstract string Type { get; }

    public RetryPolicy RetryPolicy { get; }

    public string? Status { get; private set; }

    protected SerialOptions Options { get; }

    private readonly ILogger _logger;

    /// <summary>The serial devices for <c>leddevice discover</c>: "auto" first, then every port.</summary>
    public static Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = new JsonArray((JsonNode)new JsonObject { ["value"] = "auto", ["name"] = "Auto" });
        foreach (var port in request.Context.Discovery.SerialPorts.GetPorts())
        {
            var description = port.Description.Length > 0 ? port.Description : port.PortName;
            list.Add((JsonNode)new JsonObject { ["value"] = port.PortName, ["name"] = $"{description} ({port.SystemLocation})" });
        }

        return Task.FromResult(list);
    }

    /// <summary>
    /// The port "auto" picks (port of <c>ProviderSerial::discoverFirst</c>): with the ESP handshake known HyperSerial
    /// boards first, then common ESP USB bridges; without it the first port that isn't Bluetooth; else the first port.
    /// </summary>
    public static SerialPortInfo? PickAutomatically(IReadOnlyList<SerialPortInfo> ports, bool espHandshake)
    {
        ArgumentNullException.ThrowIfNull(ports);
        for (var round = 0; round < 4; round++)
        {
            foreach (var port in ports)
            {
                if (round == 3 ||
                    (espHandshake && round == 0 && port.IsKnownEspOrPico) ||
                    (espHandshake && round == 1 && port.IsLikelyEsp) ||
                    (!espHandshake && round == 2 &&
                        !port.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) &&
                        !port.SystemLocation.Contains("ttyAMA0", StringComparison.OrdinalIgnoreCase)))
                {
                    return port;
                }
            }
        }

        return null;
    }

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        ClosePort();
        var ports = _enumerator.GetPorts();
        SerialPortInfo? info;
        if (Options.IsAuto)
        {
            info = PickAutomatically(ports, Options.EspHandshake) ?? throw new IOException("No serial device found automatically.");
            LogAutoSelected(info.PortName, info.Description);
        }
        else
        {
            info = ports.FirstOrDefault(p => string.Equals(p.PortName, Options.Output, StringComparison.OrdinalIgnoreCase)) ??
                new SerialPortInfo(Options.Output, Options.Output, string.Empty, null, null);
        }

        var port = _factory.Create(info.PortName, Options.BaudRate);
        try
        {
            port.Open();
            if (Options.EspHandshake)
            {
                await HandshakeAsync(port, info, cancellationToken);
            }

            if (Options.DelayAfterConnectMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Options.DelayAfterConnectMs), _time, cancellationToken);
            }
        }
        catch
        {
            port.Dispose();
            throw;
        }

        _port = port;
        _writeTimeouts = 0;
        Status = info.PortName;
        LogOpened(info.PortName, Options.BaudRate);
    }

    public abstract bool Write(ReadOnlySpan<byte> colors);

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        var port = _port;
        if (port is null)
        {
            return;
        }

        try
        {
            // The runner sent one black frame; the C++ driver wrote three to be sure the strip is dark.
            for (var i = 1; i < BlackFramesOnClose && _black.Length > 0; i++)
            {
                WriteBlack(_black);
            }

            port.Flush();
            if (Options.EspHandshake)
            {
                port.Write(EspGoodbye);
                port.Flush();
                await ReadGoodbyeAsync(port, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            LogCloseError(ex.Message);
        }

        ClosePort();
        Status = null;
    }

    public void Dispose()
    {
        ClosePort();
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a black frame while closing (drivers that stream float colors override it).</summary>
    protected virtual void WriteBlack(ReadOnlySpan<byte> black) => Write(black);

    /// <summary>
    /// Writes one frame. Returns false when it was dropped; throws <see cref="DeviceLostException"/> when the port is
    /// gone, so the runner reopens the device.
    /// </summary>
    protected bool WriteFrame(ReadOnlySpan<byte> frame, int ledCount)
    {
        var port = _port;
        if (port is null)
        {
            return false;
        }

        if (_black.Length != ledCount * 3)
        {
            _black = new byte[ledCount * 3];
        }

        try
        {
            port.Write(frame);
            _writeTimeouts = 0;
            return true;
        }
        catch (TimeoutException)
        {
            if (++_writeTimeouts > MaxWriteTimeouts)
            {
                throw new DeviceLostException($"Timeout writing data to {port.PortName}.");
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new DeviceLostException($"Serial port error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Wakes a HyperSerial board (port of <c>EspTools::initializeEsp</c>): boards that don't reset on DTR/RTS get the
    /// hello frame, the others are reset; then up to a second for the "Awa driver" banner.
    /// </summary>
    internal async Task HandshakeAsync(ISerialPort port, SerialPortInfo info, CancellationToken cancellationToken)
    {
        if (info.VendorId == 0x2e8a && info.ProductId is 0x0a or 0x09)
        {
            LogSkipReset("RP2040/RP2350");
            port.DtrEnable = true;
            port.RtsEnable = true;
            await Task.Delay(TimeSpan.FromMilliseconds(10), _time, cancellationToken);
            port.Write(EspHello);
            port.Flush();
        }
        else if ((info.VendorId == 0x303a && info.ProductId == 0x80c2) || (info.VendorId == 0x1106 && info.ProductId == 0x3483) || Options.ForceSerialDetection)
        {
            LogSkipReset(Options.ForceSerialDetection ? "forced ESP/Pico" : "ESP32-S2");
            port.Write(EspHello);
            port.DtrEnable = true;
            port.RtsEnable = true;
            port.RtsEnable = false;
        }
        else
        {
            port.DtrEnable = true;
            port.RtsEnable = false;
            await Task.Delay(TimeSpan.FromMilliseconds(50), _time, cancellationToken);
            port.DtrEnable = false;
            port.RtsEnable = true;
            await Task.Delay(TimeSpan.FromMilliseconds(150), _time, cancellationToken);
            port.RtsEnable = false;
            await Task.Delay(TimeSpan.FromMilliseconds(100), _time, cancellationToken);
        }

        var start = _time.GetTimestamp();
        var buffer = new byte[1024];
        var text = new StringBuilder();
        while (_time.GetElapsedTime(start) < TimeSpan.FromSeconds(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = port.Read(buffer, TimeSpan.FromMilliseconds(100));
            if (n <= 0)
            {
                continue;
            }

            text.Append(Printable(buffer.AsSpan(0, n)));
            var banner = text.ToString();
            if (banner.Contains("Awa driver", StringComparison.OrdinalIgnoreCase))
            {
                var elapsed = _time.GetElapsedTime(start);
                LogFirmwareDetected(banner, elapsed);
                return;
            }
        }

        LogFirmwareNotDetected(text);
    }

    /// <summary>Keeps newlines, spaces, digits, upper and lower case letters and the ASCII punctuation up to 'Z'.</summary>
    internal static string Printable(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (var b in data)
        {
            if (b == '\n')
            {
                sb.Append(' ');
            }
            else if ((b >= ' ' && b <= 'Z') || (b >= 'a' && b <= 'z'))
            {
                sb.Append((char)b);
            }
        }

        return sb.ToString();
    }

    private async Task ReadGoodbyeAsync(ISerialPort port, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var received = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = port.Read(buffer, TimeSpan.FromMilliseconds(100));
            if (n > 0)
            {
                received.Append(Encoding.ASCII.GetString(buffer, 0, n));
                var message = received.ToString();
                if (message.Length > 32 && message.LastIndexOf('\n') > 1)
                {
                    LogGoodbye(message);
                    break;
                }
            }

            await Task.Yield();
        }
    }

    private void ClosePort()
    {
        var port = _port;
        _port = null;
        if (port is null)
        {
            return;
        }

        try
        {
            port.Close();
        }
        catch (IOException)
        {
        }

        port.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Serial port auto-discovery picked {Port} ({Description})")]
    private partial void LogAutoSelected(string port, string description);

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened serial port {Port} at {BaudRate} bps")]
    private partial void LogOpened(string port, int baudRate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Detected a {Board} board: the reset is skipped")]
    private partial void LogSkipReset(string board);

    [LoggerMessage(Level = LogLevel.Information, Message = "Detected a device with the HyperSerial firmware ({Banner}) after {Elapsed}")]
    private partial void LogFirmwareDetected(string banner, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not detect a HyperSerialEsp8266/HyperSerialESP32/HyperSerialPico device (received: '{Received}')")]
    private partial void LogFirmwareNotDetected(StringBuilder received);

    [LoggerMessage(Level = LogLevel.Information, Message = "Received goodbye: '{Message}'")]
    private partial void LogGoodbye(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't close the serial port cleanly: {Reason}")]
    private partial void LogCloseError(string reason);
}
