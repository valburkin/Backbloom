using System.Collections.Concurrent;
using System.Net;
using Backbloom.Discovery.Mdns;

namespace Backbloom.Discovery.Tests;

/// <summary>An mDNS transport in memory: records what is sent and delivers datagrams on request.</summary>
internal sealed class FakeTransport(bool canAdvertise = true) : IMdnsTransport
{
    private Action<byte[], IPEndPoint>? _received;

    public bool CanAdvertise => canAdvertise;

    public int Starts { get; private set; }

    public int Disposals { get; private set; }

    public ConcurrentQueue<byte[]> Multicast { get; } = new();

    public ConcurrentQueue<(byte[] Message, IPEndPoint To)> Unicast { get; } = new();

    public Action<byte[]>? OnSend { get; set; }

    /// <summary>Thrown by <see cref="SendMulticast"/> when set (a transport closed under the sender).</summary>
    public Exception? SendFailure { get; set; }

    public void Start(Action<byte[], IPEndPoint> received)
    {
        Starts++;
        _received = received;
    }

    public void SendMulticast(byte[] message)
    {
        if (SendFailure is { } failure)
        {
            throw failure;
        }

        Multicast.Enqueue(message);
        OnSend?.Invoke(message);
    }

    public void SendUnicast(byte[] message, IPEndPoint destination) => Unicast.Enqueue((message, destination));

    public void Deliver(byte[] message, IPEndPoint from) => _received!(message, from);

    public void Dispose() => Disposals++;
}
