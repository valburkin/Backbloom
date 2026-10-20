using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Overglow.Discovery.Mdns;

/// <summary>Moves mDNS datagrams; abstracted so the protocol logic can be tested without multicast sockets.</summary>
public interface IMdnsTransport : IDisposable
{
    /// <summary>True when the transport listens on the mDNS port and can therefore answer queries (advertise).</summary>
    bool CanAdvertise { get; }

    /// <summary>Starts receiving; <paramref name="received"/> is called on a background thread.</summary>
    void Start(Action<byte[], IPEndPoint> received);

    /// <summary>Sends to the mDNS multicast group on every network interface.</summary>
    void SendMulticast(byte[] message);

    void SendUnicast(byte[] message, IPEndPoint destination);
}

/// <summary>
/// IPv4 mDNS socket: shares UDP port 5353 with the operating system's own responder (address reuse) and joins
/// 224.0.0.251 on every multicast-capable interface. When the port can't be shared it falls back to an ephemeral port:
/// queries from there are "legacy unicast" queries (RFC 6762 6.7) that responders answer directly, and nothing is
/// advertised.
/// </summary>
public sealed partial class UdpMdnsTransport(ILogger<UdpMdnsTransport> logger, int port = UdpMdnsTransport.MdnsPort) : IMdnsTransport
{
    public const int MdnsPort = 5353;

    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    private readonly Lock _sendLock = new();
    private readonly HashSet<IPAddress> _joined = [];
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private Task? _receiveLoop;

    public bool CanAdvertise { get; private set; }

    public void Start(Action<byte[], IPEndPoint> received)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (_socket is not null)
        {
            return;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            CanAdvertise = true;
        }
        catch (SocketException ex)
        {
            LogPortInUse(port, ex.SocketErrorCode);
            socket.Dispose();
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        _socket = socket;
        JoinInterfaces();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, received));
    }

    public void SendMulticast(byte[] message)
    {
        var socket = _socket ?? throw new InvalidOperationException("The mDNS transport is not started.");
        lock (_sendLock)
        {
            JoinInterfaces();
            foreach (var local in InterfaceAddresses())
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    socket.SendTo(message, new IPEndPoint(Group, MdnsPort));
                }
                catch (SocketException ex)
                {
                    LogSendFailed(local, ex.SocketErrorCode);
                }
            }
        }
    }

    public void SendUnicast(byte[] message, IPEndPoint destination)
    {
        var socket = _socket ?? throw new InvalidOperationException("The mDNS transport is not started.");
        lock (_sendLock)
        {
            try
            {
                socket.SendTo(message, destination);
            }
            catch (SocketException ex)
            {
                LogSendFailed(destination.Address, ex.SocketErrorCode);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket?.Dispose();
        try
        {
            _receiveLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    /// <summary>IPv4 addresses of the interfaces that are up and support multicast (no loopback, no tunnels).</summary>
    public static IEnumerable<IPAddress> InterfaceAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                {
                    yield return address.Address;
                }
            }
        }
    }

    private void JoinInterfaces()
    {
        if (_socket is not { } socket || !CanAdvertise)
        {
            return;
        }

        foreach (var local in InterfaceAddresses())
        {
            if (_joined.Add(local))
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
                }
                catch (SocketException ex)
                {
                    LogJoinFailed(local, ex.SocketErrorCode);
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, Action<byte[], IPEndPoint> received)
    {
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }

            received(buffer.AsSpan(0, result.ReceivedBytes).ToArray(), (IPEndPoint)result.RemoteEndPoint);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "mDNS port {Port} can't be shared ({Error}): devices are still found, but the web UI isn't advertised")]
    private partial void LogPortInUse(int port, SocketError error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "mDNS: couldn't send on {Interface}: {Error}")]
    private partial void LogSendFailed(IPAddress @interface, SocketError error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "mDNS: couldn't join the multicast group on {Interface}: {Error}")]
    private partial void LogJoinFailed(IPAddress @interface, SocketError error);
}
