using System.Net;
using System.Net.Sockets;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Overglow.Devices.Transport;

/// <summary>Settings of a DTLS 1.2 PSK connection (Hue Entertainment: TLS_PSK_WITH_AES_128_GCM_SHA256, port 2100).</summary>
/// <param name="Identity">PSK identity (the Hue username / application key).</param>
/// <param name="Key">The pre-shared key (the Hue client key, hex-decoded).</param>
/// <param name="HandshakeTimeout">Time allowed for the whole handshake.</param>
/// <param name="ResendTimeout">First retransmission timeout of handshake flights (doubles on each resend).</param>
public sealed record DtlsPskSettings(IPEndPoint Remote, byte[] Identity, byte[] Key, TimeSpan HandshakeTimeout, TimeSpan ResendTimeout);

/// <summary>
/// A DTLS 1.2 client with a pre-shared key over a connected UDP socket, using BouncyCastle (MIT) because .NET has no
/// DTLS (port of <c>ProviderUdpSSL</c>, which used QDtls + OpenSSL).
/// </summary>
public sealed class DtlsPskChannel : IDisposable
{
    private readonly Socket _socket;
    private readonly DtlsTransport _transport;

    private DtlsPskChannel(Socket socket, DtlsTransport transport)
    {
        _socket = socket;
        _transport = transport;
    }

    /// <summary>Connects and runs the handshake; throws <see cref="IOException"/> (TlsFatalAlert, timeout) when it fails.</summary>
    public static DtlsPskChannel Connect(DtlsPskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var socket = new Socket(settings.Remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            LocalBinding.BindIfLoopback(socket, settings.Remote);
            socket.Connect(settings.Remote);
            var client = new PskClient(new BcTlsCrypto(new SecureRandom()), settings);
            var transport = new DtlsClientProtocol().Connect(client, new UdpTransport(socket));
            return new DtlsPskChannel(socket, transport);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Sends one encrypted datagram; false when the connection failed.</summary>
    public bool Send(byte[] buffer, int offset, int length)
    {
        try
        {
            _transport.Send(buffer, offset, length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TlsFatalAlert)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _transport.Close();
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
        }

        _socket.Dispose();
    }

    private sealed class PskClient(BcTlsCrypto crypto, DtlsPskSettings settings) : PskTlsClient(crypto, new BasicTlsPskIdentity(settings.Identity, settings.Key))
    {
        public override int GetHandshakeTimeoutMillis() => (int)settings.HandshakeTimeout.TotalMilliseconds;

        public override int GetHandshakeResendTimeMillis() => (int)settings.ResendTimeout.TotalMilliseconds;

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
    }

    /// <summary>BouncyCastle datagram transport over a connected UDP socket.</summary>
    internal sealed class UdpTransport(Socket socket) : DatagramTransport
    {
        private const int Mtu = 1500 - 20 - 8;

        public int GetReceiveLimit() => Mtu;

        public int GetSendLimit() => Mtu;

        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);

        public int Receive(Span<byte> buffer, int waitMillis)
        {
            if (!socket.Poll(Math.Clamp(waitMillis, 0, int.MaxValue / 1000) * 1000, SelectMode.SelectRead))
            {
                return -1;
            }

            try
            {
                return socket.Receive(buffer, SocketFlags.None);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                return -1;
            }
        }

        public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));

        public void Send(ReadOnlySpan<byte> buffer) => socket.Send(buffer, SocketFlags.None);

        public void Close()
        {
        }
    }
}
