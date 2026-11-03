using Microsoft.Extensions.Logging;
using Backbloom.Devices.Transport;
using Backbloom.Discovery.Usb;

namespace Backbloom.Devices.Setup;

/// <summary>An ESP board on a USB serial port that answered Improv.</summary>
/// <param name="Name">The port's description (the USB bridge's name).</param>
public sealed record UsbBoard(string Port, string Name, ImprovState State, ImprovDeviceInfo? Info, string? Address);

/// <summary>What a USB scan found: the boards that answered Improv, and ESP ports that didn't (no WLED on them, or busy).</summary>
public sealed record UsbScan(IReadOnlyList<UsbBoard> Boards, IReadOnlyList<SerialPortInfo> Silent);

/// <summary>How long the Improv steps wait.</summary>
/// <param name="Probe">For the first answer to a probe.</param>
/// <param name="ProbeAgain">For the second probe, when the first got no answer (a board that was reset by opening the port is starting).</param>
/// <param name="Scan">For the list of Wi-Fi networks (a scan takes a few seconds).</param>
/// <param name="Join">For joining a network.</param>
public sealed record ImprovTimeouts(TimeSpan Probe, TimeSpan ProbeAgain, TimeSpan Scan, TimeSpan Join)
{
    public static ImprovTimeouts Default { get; } = new(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
}

/// <summary>
/// The guided setup's USB step: finds ESP boards on USB serial ports that answer Improv Wi-Fi, lists the networks a
/// board sees and puts it on Wi-Fi. Only ports of likely ESP boards (<see cref="SerialPortInfo.IsLikelyEsp"/>) that
/// no configured device uses are opened, at 115200 baud with DTR and RTS off (so opening the port doesn't reset
/// the board), one port at a time, and closed again. Wi-Fi passwords go to the board only: they are never logged
/// or stored.
/// </summary>
public sealed partial class UsbBoards : IDisposable
{
    public const int BaudRate = 115200;

    private readonly ISerialPortFactory _ports;
    private readonly ISerialPortEnumerator _enumerator;
    private readonly ImprovTimeouts _timeouts;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UsbBoards(DriverContext context, ImprovTimeouts? timeouts = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ports = context.SerialPorts;
        _enumerator = context.Discovery.SerialPorts;
        _timeouts = timeouts ?? ImprovTimeouts.Default;
        _logger = context.LoggerFactory.CreateLogger<UsbBoards>();
    }

    /// <summary>The ESP ports that may be opened: likely ESP boards that none of <paramref name="inUse"/> names.</summary>
    public IReadOnlyList<SerialPortInfo> Candidates(IReadOnlyCollection<string> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        return [.. _enumerator.GetPorts().Where(p => p.IsLikelyEsp && !inUse.Contains(p.PortName, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>Probes every candidate port (see <see cref="Candidates"/>).</summary>
    public async Task<UsbScan> ScanAsync(IReadOnlyCollection<string> inUse, CancellationToken cancellationToken)
    {
        var boards = new List<UsbBoard>();
        var silent = new List<SerialPortInfo>();
        foreach (var port in Candidates(inUse))
        {
            ImprovBoardState? state;
            try
            {
                state = await WithPortAsync(port.PortName, client =>
                    client.Probe(_timeouts.Probe, cancellationToken) ?? client.Probe(_timeouts.ProbeAgain, cancellationToken), cancellationToken);
            }
            catch (HardwareSetupException ex)
            {
                LogPortSkipped(port.PortName, ex.Message);
                silent.Add(port);
                continue;
            }

            if (state is null)
            {
                LogNoImprov(port.PortName);
                silent.Add(port);
                continue;
            }

            LogBoard(port.PortName, state.Info?.Firmware ?? "?", state.Info?.Version ?? "?", state.Info?.Chip ?? "?", state.State);
            boards.Add(new UsbBoard(port.PortName, port.Description.Length > 0 ? port.Description : port.PortName, state.State, state.Info, state.Address));
        }

        return new UsbScan(boards, silent);
    }

    /// <summary>The Wi-Fi networks the board on <paramref name="port"/> sees: strongest first, one entry per name.</summary>
    public async Task<IReadOnlyList<ImprovNetwork>> NetworksAsync(string port, IReadOnlyCollection<string> inUse, CancellationToken cancellationToken)
    {
        CheckPort(port, inUse);
        var found = await WithPortAsync(port, client => client.ScanNetworks(_timeouts.Scan, cancellationToken), cancellationToken);
        return [.. found
            .Where(n => n.Ssid.Length > 0)
            .GroupBy(n => n.Ssid, StringComparer.Ordinal)
            .Select(g => g.MaxBy(n => n.Rssi)! with { Secure = g.Any(n => n.Secure) })
            .OrderByDescending(n => n.Rssi)
            .ThenBy(n => n.Ssid, StringComparer.Ordinal)];
    }

    /// <summary>Puts the board on <paramref name="port"/> on Wi-Fi; returns the URL it reports (null when none).</summary>
    public async Task<string?> JoinWifiAsync(string port, string ssid, string password, IReadOnlyCollection<string> inUse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ssid);
        ArgumentNullException.ThrowIfNull(password);
        CheckPort(port, inUse);
        if (ssid.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(ssid) > 32)
        {
            throw new HardwareSetupException("The network name must have 1 to 32 characters");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(password) > 64)
        {
            throw new HardwareSetupException("The Wi-Fi password can have 64 characters at most");
        }

        LogJoining(port, ssid);
        var address = await WithPortAsync(port, client => client.SendWifi(ssid, password, _timeouts.Join, cancellationToken), cancellationToken);
        LogJoined(port, ssid, address ?? "?");
        return address;
    }

    public void Dispose() => _gate.Dispose();

    private void CheckPort(string port, IReadOnlyCollection<string> inUse)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (!Candidates(inUse).Any(p => string.Equals(p.PortName, port, StringComparison.OrdinalIgnoreCase)))
        {
            throw new HardwareSetupException(inUse.Contains(port, StringComparer.OrdinalIgnoreCase)
                ? $"{port} is used by one of your devices"
                : $"There is no ESP board on {port}");
        }
    }

    // One port at a time: open (DTR and RTS off), run the Improv exchange on the thread pool, close.
    private async Task<T> WithPortAsync<T>(string portName, Func<ImprovClient, T> exchange, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () =>
                {
                    using var port = _ports.Create(portName, BaudRate);
                    port.DtrEnable = false;
                    port.RtsEnable = false;
                    try
                    {
                        port.Open();
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
                    {
                        throw new HardwareSetupException($"{portName} is in use by another program", ex);
                    }

                    try
                    {
                        return exchange(new ImprovClient(port));
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                    {
                        throw new HardwareSetupException($"The connection to the board on {portName} failed: {ex.Message}", ex);
                    }
                    finally
                    {
                        port.Close();
                    }
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "USB {Port}: Improv board {Firmware} {Version} ({Chip}), {State}")]
    private partial void LogBoard(string port, string firmware, string version, string chip, ImprovState state);

    [LoggerMessage(Level = LogLevel.Debug, Message = "USB {Port}: no Improv answer")]
    private partial void LogNoImprov(string port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "USB {Port}: skipped ({Reason})")]
    private partial void LogPortSkipped(string port, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "USB {Port}: sending the Wi-Fi settings for {Ssid} to the board")]
    private partial void LogJoining(string port, string ssid);

    [LoggerMessage(Level = LogLevel.Information, Message = "USB {Port}: the board joined {Ssid} ({Address})")]
    private partial void LogJoined(string port, string ssid, string address);
}
