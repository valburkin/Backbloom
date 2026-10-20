using System.Net;
using System.Net.Sockets;

namespace Backbloom.Devices.Transport;

/// <summary>A connected datagram (UDP) socket. Abstracted so packet tests can capture what a driver sends.</summary>
public interface IDatagramChannel : IDisposable
{
    void Connect(IPEndPoint remote);

    /// <summary>Sends one datagram without allocating; false when the socket reported an error.</summary>
    bool Send(ReadOnlySpan<byte> datagram);
}

/// <summary>UDP socket connected to one controller (a connected socket sends without per-call allocations).</summary>
public sealed class UdpDatagramChannel : IDatagramChannel
{
    private Socket? _socket;

    public void Connect(IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        _socket?.Dispose();
        _socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        LocalBinding.BindIfLoopback(_socket, remote);
        _socket.Connect(remote);
    }

    public bool Send(ReadOnlySpan<byte> datagram)
    {
        var socket = _socket ?? throw new InvalidOperationException("The UDP channel is not connected.");
        try
        {
            return socket.Send(datagram, SocketFlags.None) == datagram.Length;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _socket = null;
    }
}
