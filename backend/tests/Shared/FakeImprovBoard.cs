using System.Collections.Concurrent;
using System.Text;
using Backbloom.Devices.Setup;
using Backbloom.Devices.Transport;

namespace Backbloom.Tests.Fakes;

/// <summary>
/// A WLED board on a USB serial port as Improv sees it: answers the RPC commands it is sent with state, error and
/// result packets (a boot message and newlines in between, as real boards print). Nothing real is opened.
/// </summary>
internal sealed class FakeImprovBoard(string portName) : ISerialPort
{
    private readonly ConcurrentQueue<byte> _output = new();
    private readonly List<byte> _input = [];

    public string PortName { get; } = portName;

    public bool IsOpen { get; private set; }

    public bool DtrEnable { get; set; } = true;

    public bool RtsEnable { get; set; } = true;

    /// <summary>DTR and RTS when the port was opened (both must be off, or the board resets).</summary>
    public (bool Dtr, bool Rts)? SignalsAtOpen { get; private set; }

    public int Opens { get; private set; }

    public int Closes { get; private set; }

    /// <summary>Answers nothing (not an Improv board).</summary>
    public bool Silent { get; set; }

    /// <summary>Opening fails (another program has the port).</summary>
    public bool Busy { get; set; }

    public ImprovState State { get; set; } = ImprovState.Ready;

    public string Firmware { get; set; } = "WLED";

    public string Version { get; set; } = "0.15.0";

    public string Chip { get; set; } = "esp32";

    public string DeviceName { get; set; } = "WLED";

    /// <summary>The address it reports once on Wi-Fi.</summary>
    public string Url { get; set; } = "http://192.0.2.50";

    /// <summary>The networks its scan finds: SSID, RSSI, whether a password is needed.</summary>
    public List<(string Ssid, int Rssi, bool Secure)> Networks { get; } = [];

    /// <summary>The password that joins a network; any other is "unable to connect".</summary>
    public string Password { get; set; } = "right-password";

    /// <summary>Joining a network waits until this is set (the writer waits, as a slow board keeps the request busy).</summary>
    public ManualResetEventSlim? JoinGate { get; set; }

    /// <summary>The SSID of the last Wi-Fi settings received.</summary>
    public string? JoinedSsid { get; private set; }

    public List<ImprovCommand> Commands { get; } = [];

    public int BytesToRead => _output.Count;

    public void Open()
    {
        if (Busy)
        {
            throw new UnauthorizedAccessException($"Access to the port '{PortName}' is denied.");
        }

        SignalsAtOpen = (DtrEnable, RtsEnable);
        IsOpen = true;
        Opens++;
        Emit(Encoding.ASCII.GetBytes("\r\nets Jun  8 2016 00:22:57\r\n"));
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The port is closed.");
        }

        _input.AddRange(data.ToArray());
        while (ImprovProtocol.TryRead(_input) is { } packet)
        {
            if (packet.Type == ImprovPacketType.RpcCommand && packet.Data.Length >= 2)
            {
                Commands.Add((ImprovCommand)packet.Data[0]);
            }

            if (packet.Type == ImprovPacketType.RpcCommand && packet.Data.Length >= 2 && !Silent)
            {
                Handle((ImprovCommand)packet.Data[0], packet.Data.AsSpan(2, Math.Min(packet.Data[1], packet.Data.Length - 2)).ToArray());
            }
        }
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        var n = 0;
        while (n < buffer.Length && _output.TryDequeue(out var b))
        {
            buffer[n++] = b;
        }

        if (n == 0)
        {
            Thread.Sleep(Math.Min(5, Math.Max(1, (int)timeout.TotalMilliseconds)));
        }

        return n;
    }

    public void Flush()
    {
    }

    public void Close()
    {
        if (IsOpen)
        {
            Closes++;
        }

        IsOpen = false;
    }

    public void Dispose() => Close();

    private void Handle(ImprovCommand command, byte[] data)
    {
        Emit(ImprovProtocol.Packet(ImprovPacketType.ErrorState, [(byte)ImprovError.None]));
        switch (command)
        {
            case ImprovCommand.RequestCurrentState:
                EmitState();
                if (State == ImprovState.Provisioned)
                {
                    Emit(ImprovProtocol.Result(ImprovCommand.RequestCurrentState, Url));
                }

                break;
            case ImprovCommand.RequestDeviceInfo:
                Emit(ImprovProtocol.Result(ImprovCommand.RequestDeviceInfo, Firmware, Version, Chip, DeviceName));
                break;
            case ImprovCommand.RequestScannedNetworks:
                foreach (var (network, rssi, secure) in Networks)
                {
                    Emit(ImprovProtocol.Result(ImprovCommand.RequestScannedNetworks, network, rssi.ToString(System.Globalization.CultureInfo.InvariantCulture), secure ? "YES" : "NO"));
                }

                Emit(ImprovProtocol.Result(ImprovCommand.RequestScannedNetworks));
                break;
            case ImprovCommand.SendWifiSettings:
                var ssid = Encoding.UTF8.GetString(data, 1, data[0]);
                var password = Encoding.UTF8.GetString(data, 2 + data[0], data[1 + data[0]]);
                JoinedSsid = ssid;
                JoinGate?.Wait(TimeSpan.FromSeconds(10));
                State = ImprovState.Provisioning;
                EmitState();
                if (password == Password)
                {
                    State = ImprovState.Provisioned;
                    EmitState();
                    Emit(ImprovProtocol.Result(ImprovCommand.SendWifiSettings, Url));
                }
                else
                {
                    State = ImprovState.Ready;
                    Emit(ImprovProtocol.Packet(ImprovPacketType.ErrorState, [(byte)ImprovError.UnableToConnect]));
                    EmitState();
                }

                break;
            default:
                Emit(ImprovProtocol.Packet(ImprovPacketType.ErrorState, [(byte)ImprovError.UnknownRpcCommand]));
                break;
        }
    }

    private void EmitState() => Emit(ImprovProtocol.Packet(ImprovPacketType.CurrentState, [(byte)State]));

    private void Emit(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            _output.Enqueue(b);
        }
    }
}

/// <summary>Hands out the fake boards by port name; a port without one is an empty port (nothing answers).</summary>
internal sealed class FakeImprovPorts : ISerialPortFactory
{
    public Dictionary<string, FakeImprovBoard> Boards { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Port, int BaudRate)> Created { get; } = [];

    public FakeImprovBoard Add(string port)
    {
        var board = new FakeImprovBoard(port);
        Boards[port] = board;
        return board;
    }

    public ISerialPort Create(string portName, int baudRate)
    {
        Created.Add((portName, baudRate));
        return Boards.TryGetValue(portName, out var board) ? board : new FakeImprovBoard(portName) { Silent = true };
    }
}
