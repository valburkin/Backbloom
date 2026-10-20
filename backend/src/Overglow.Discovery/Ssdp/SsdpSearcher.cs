using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Overglow.Discovery.Ssdp;

/// <summary>One answer to an M-SEARCH request: the HTTP-like headers (names in upper case) and who sent it.</summary>
public sealed record SsdpResponse(IPEndPoint From, IReadOnlyDictionary<string, string> Headers)
{
    public string Header(string name) => Headers.TryGetValue(name.ToUpperInvariant(), out var v) ? v : string.Empty;

    public string Location => Header("LOCATION");
}

/// <summary>Sends SSDP searches (UPnP 1.1 M-SEARCH) and collects the answers.</summary>
public interface ISsdpSearcher
{
    /// <param name="searchTarget">The ST header, e.g. "upnp:rootdevice" or "wifi_bulb" (Yeelight).</param>
    /// <param name="port">1900 (UPnP) or 1982 (Yeelight).</param>
    /// <param name="timeout">How long to collect answers.</param>
    Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Port of <c>SSDPDiscover</c> (sources/ssdp/SSDPDiscover.cpp): one M-SEARCH to 239.255.255.250, then every answer
/// within the timeout. Passive in the sense that it only asks who is there.
/// </summary>
public sealed partial class SsdpSearcher(Func<int, IPEndPoint>? destination = null) : ISsdpSearcher
{
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");

    private readonly Func<int, IPEndPoint> _destination = destination ?? (port => new IPEndPoint(MulticastAddress, port));

    /// <summary>The search request exactly as the C++ code formats it (MX 1).</summary>
    public static string SearchRequest(IPEndPoint destination, string searchTarget, int maxWaitSeconds = 1)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"M-SEARCH * HTTP/1.1\r\nHOST: {destination.Address}:{destination.Port}\r\nMAN: \"ssdp:discover\"\r\nMX: {maxWaitSeconds}\r\nST: {searchTarget}\r\n\r\n");
    }

    /// <summary>Parses an answer like the C++ code: every "Name: value" line, names upper case, values trimmed.</summary>
    public static IReadOnlyDictionary<string, string> ParseHeaders(string datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in datagram.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Contains("HTTP/1.1", StringComparison.Ordinal))
            {
                continue;
            }

            var entry = Whitespace().Replace(raw.Trim(), " ");
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            headers[entry[..colon].Trim().ToUpperInvariant()] = entry[(colon + 1)..].Trim();
        }

        return headers;
    }

    public async Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchTarget);
        var destination = _destination(port);
        using var socket = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(destination.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        if (destination.Address.Equals(MulticastAddress))
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
        }

        await socket.SendToAsync(Encoding.UTF8.GetBytes(SearchRequest(destination, searchTarget)), SocketFlags.None, destination, cancellationToken);

        var responses = new List<SsdpResponse>();
        var buffer = new byte[4096];
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(timeout);
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, window.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
            responses.Add(new SsdpResponse((IPEndPoint)result.RemoteEndPoint, ParseHeaders(text)));
        }

        return responses;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
