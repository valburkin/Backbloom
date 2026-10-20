using System.Net;
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
/// The responder's socket (only while the web UI is advertised, i.e. remote access is on): shares UDP port 5353 with
/// the operating system's own responder (address reuse) and joins 224.0.0.251 on every multicast-capable interface,
/// so it hears and answers the queries of phones. This is a listener on the network, which is why it only exists
/// after the user turned remote access on. When the port can't be shared, nothing is advertised.
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
        }
        catch (SocketException ex)
        {
            LogPortInUse(port, ex.SocketErrorCode);
            socket.Dispose();
            return;
        }

        CanAdvertise = true;
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
            foreach (var local in LocalNetwork.MulticastAddresses())
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

    private void JoinInterfaces()
    {
        if (_socket is not { } socket)
        {
            return;
        }

        foreach (var local in LocalNetwork.MulticastAddresses())
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "mDNS port {Port} can't be shared ({Error}): the web UI isn't advertised")]
    private partial void LogPortInUse(int port, SocketError error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "mDNS: couldn't send on {Interface}: {Error}")]
    private partial void LogSendFailed(IPAddress @interface, SocketError error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "mDNS: couldn't join the multicast group on {Interface}: {Error}")]
    private partial void LogJoinFailed(IPAddress @interface, SocketError error);
}

/// <summary>
/// The sockets of one browse: <see cref="QuerySockets"/> (an ephemeral port per interface, open only during the
/// browse). Queries from a port other than 5353 are "legacy unicast" queries (RFC 6762 6.7) and carry the QU bit, so
/// responders answer directly to that port; nothing joins the multicast group or binds port 5353.
/// </summary>
/// <param name="locals">Addresses to send from; every interface that can multicast by default (tests: loopback).</param>
/// <param name="group">Where queries go; the mDNS group by default (tests: a loopback responder).</param>
public sealed class UdpMdnsQueryTransport(Func<IEnumerable<IPAddress>>? locals = null, IPEndPoint? group = null) : IMdnsTransport
{
    private readonly IPEndPoint _group = group ?? new IPEndPoint(UdpMdnsTransport.Group, UdpMdnsTransport.MdnsPort);
    private readonly CancellationTokenSource _stop = new();
    private QuerySockets? _sockets;
    private Task? _receiving;

    public bool CanAdvertise => false;

    /// <summary>Where the browse's sockets are bound while it runs (empty before <see cref="Start"/>).</summary>
    public IReadOnlyList<IPEndPoint> LocalEndPoints => _sockets?.LocalEndPoints ?? [];

    public void Start(Action<byte[], IPEndPoint> received)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (_sockets is not null)
        {
            return;
        }

        _sockets = QuerySockets.Open(locals?.Invoke() ?? LocalNetwork.MulticastAddresses(), multicastTtl: 255);
        _receiving = _sockets.ReceiveAsync(received, _stop.Token);
    }

    public void SendMulticast(byte[] message)
    {
        var sockets = _sockets ?? throw new InvalidOperationException("The mDNS transport is not started.");
        sockets.Send(message, _ => _group);
    }

    public void SendUnicast(byte[] message, IPEndPoint destination)
    {
        var sockets = _sockets ?? throw new InvalidOperationException("The mDNS transport is not started.");
        sockets.Send(message, _ => destination);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _sockets?.Dispose();
        try
        {
            _receiving?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
