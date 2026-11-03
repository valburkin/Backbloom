using System.Diagnostics;
using System.Globalization;
using Backbloom.Devices.Transport;

namespace Backbloom.Devices.Setup;

/// <summary>What an Improv device says about itself (RPC "request device information").</summary>
public sealed record ImprovDeviceInfo(string Firmware, string Version, string Chip, string DeviceName);

/// <summary>A board's answer to a probe: its state, its info and, when it is on Wi-Fi, the URL it reports.</summary>
public sealed record ImprovBoardState(ImprovState State, ImprovDeviceInfo? Info, string? Address);

/// <summary>A Wi-Fi network the board found.</summary>
/// <param name="Rssi">Signal strength in dBm.</param>
public sealed record ImprovNetwork(string Ssid, int Rssi, bool Secure)
{
    /// <summary>The signal in percent, as WLED shows it: 2 x (RSSI + 100), 0..100.</summary>
    public int SignalPercent => Math.Clamp(2 * (Rssi + 100), 0, 100);
}

/// <summary>
/// The client side of Improv Wi-Fi over a serial port (https://www.improv-wifi.com/serial/): sends RPC commands and
/// reads the board's state, error and result packets. Blocking, one request at a time; the caller opens and closes
/// the port.
/// </summary>
public sealed class ImprovClient(ISerialPort port)
{
    private static readonly TimeSpan s_readSlice = TimeSpan.FromMilliseconds(100);

    // how long a board that reported "provisioned" may take to send its address afterwards
    private static readonly TimeSpan s_addressGrace = TimeSpan.FromSeconds(3);

    private readonly List<byte> _received = [];
    private readonly byte[] _chunk = new byte[512];

    public void Send(ImprovCommand command, ReadOnlySpan<byte> payload = default) => port.Write(ImprovProtocol.Command(command, payload));

    /// <summary>The next packet, waiting up to <paramref name="timeout"/>; null when none came.</summary>
    public ImprovPacket? Receive(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (ImprovProtocol.TryRead(_received) is { } packet)
            {
                return packet;
            }

            var left = timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var n = port.Read(_chunk, left < s_readSlice ? left : s_readSlice);
            for (var i = 0; i < n; i++)
            {
                _received.Add(_chunk[i]);
            }
        }
    }

    /// <summary>
    /// Asks for the state and the device info; null when nothing Improv came back within <paramref name="timeout"/>
    /// (not an Improv board, or it is still starting).
    /// </summary>
    public ImprovBoardState? Probe(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Send(ImprovCommand.RequestCurrentState);
        Send(ImprovCommand.RequestDeviceInfo);
        ImprovState? state = null;
        ImprovDeviceInfo? info = null;
        string? address = null;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (Receive(timeout - clock.Elapsed, cancellationToken) is not { } packet)
            {
                break;
            }

            switch (packet.Type)
            {
                case ImprovPacketType.CurrentState when packet.Data.Length > 0:
                    state = (ImprovState)packet.Data[0];
                    break;
                case ImprovPacketType.RpcResult:
                    var (command, strings) = packet.ReadResult();
                    if (command == ImprovCommand.RequestDeviceInfo && strings.Count >= 4)
                    {
                        info = new ImprovDeviceInfo(strings[0], strings[1], strings[2], strings[3]);
                    }
                    else if (command is ImprovCommand.RequestCurrentState or ImprovCommand.SendWifiSettings && strings.Count > 0)
                    {
                        address = strings[0];
                    }

                    break;
            }

            if (state is { } s && info is not null && (s != ImprovState.Provisioned || address is not null))
            {
                break;
            }
        }

        return state is null && info is null ? null : new ImprovBoardState(state ?? ImprovState.Ready, info, address);
    }

    /// <summary>The networks the board finds (one result per network, an empty result ends the list).</summary>
    public IReadOnlyList<ImprovNetwork> ScanNetworks(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Send(ImprovCommand.RequestScannedNetworks);
        var networks = new List<ImprovNetwork>();
        var clock = Stopwatch.StartNew();
        while (Receive(timeout - clock.Elapsed, cancellationToken) is { } packet)
        {
            if (packet.Type == ImprovPacketType.ErrorState && packet.Data is [var error, ..] && error != (byte)ImprovError.None)
            {
                throw new HardwareSetupException(Describe((ImprovError)error, "list the Wi-Fi networks"));
            }

            if (packet.Type != ImprovPacketType.RpcResult)
            {
                continue;
            }

            var (command, strings) = packet.ReadResult();
            if (command != ImprovCommand.RequestScannedNetworks)
            {
                continue;
            }

            if (strings.Count == 0)
            {
                return networks;
            }

            if (strings.Count >= 3)
            {
                var rssi = int.TryParse(strings[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : -100;
                networks.Add(new ImprovNetwork(strings[0], rssi, string.Equals(strings[2], "YES", StringComparison.OrdinalIgnoreCase)));
            }
        }

        throw new HardwareSetupException("The board didn’t list the Wi-Fi networks in time");
    }

    /// <summary>
    /// Sends the Wi-Fi settings and waits until the board joined: returns the URL it reports (null when it reported
    /// none). Throws <see cref="HardwareSetupException"/> when it couldn't join or didn't answer in time.
    /// </summary>
    public string? SendWifi(string ssid, string password, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Send(ImprovCommand.SendWifiSettings, ImprovProtocol.WifiSettings(ssid, password));
        var clock = Stopwatch.StartNew();
        TimeSpan? provisionedAt = null;
        while (true)
        {
            var left = timeout - clock.Elapsed;
            if (provisionedAt is { } at && at + s_addressGrace - clock.Elapsed < left)
            {
                left = at + s_addressGrace - clock.Elapsed;
            }

            if (Receive(left, cancellationToken) is not { } packet)
            {
                if (provisionedAt is not null)
                {
                    return null;
                }

                throw new HardwareSetupException($"The board didn’t join {ssid} in time: check the network name and password");
            }

            switch (packet.Type)
            {
                case ImprovPacketType.ErrorState when packet.Data is [var error, ..] && error != (byte)ImprovError.None:
                    throw new HardwareSetupException((ImprovError)error == ImprovError.UnableToConnect
                        ? $"couldn’t join {ssid}: check the password"
                        : Describe((ImprovError)error, $"join {ssid}"));
                case ImprovPacketType.CurrentState when packet.Data is [var state, ..]:
                    if ((ImprovState)state == ImprovState.Provisioned)
                    {
                        provisionedAt ??= clock.Elapsed;
                    }
                    else if ((ImprovState)state == ImprovState.AuthorizationRequired)
                    {
                        throw new HardwareSetupException("Press the button on the board, then try again");
                    }

                    break;
                case ImprovPacketType.RpcResult:
                    var (command, strings) = packet.ReadResult();
                    if (command == ImprovCommand.SendWifiSettings)
                    {
                        return strings.Count > 0 && strings[0].Length > 0 ? strings[0] : null;
                    }

                    break;
            }
        }
    }

    private static string Describe(ImprovError error, string what) => error switch
    {
        ImprovError.InvalidRpcPacket => $"The board couldn’t read the request to {what}",
        ImprovError.UnknownRpcCommand => $"The board can’t {what} over USB (its firmware doesn’t support it)",
        ImprovError.UnableToConnect => $"The board couldn’t {what}",
        ImprovError.NotAuthorized => "Press the button on the board, then try again",
        _ => $"The board couldn’t {what} (error {(byte)error})",
    };
}
