using System.Globalization;
using System.Net;
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
/// within the timeout. Passive in the sense that it only asks who is there. The search goes out from
/// <see cref="QuerySockets"/> (one ephemeral socket per interface, closed afterwards), where the answers arrive.
/// </summary>
/// <param name="destination">Where to send for a port; the SSDP group by default (tests: a loopback responder).</param>
/// <param name="locals">Addresses to send from; every interface that can multicast by default (loopback for a loopback destination).</param>
public sealed partial class SsdpSearcher(Func<int, IPEndPoint>? destination = null, Func<IEnumerable<IPAddress>>? locals = null) : ISsdpSearcher
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
        var from = locals?.Invoke() ?? (IPAddress.IsLoopback(destination.Address) ? [destination.Address] : LocalNetwork.MulticastAddresses());
        using var sockets = QuerySockets.Open(from, multicastTtl: destination.Address.Equals(MulticastAddress) ? 4 : null);
        var request = Encoding.UTF8.GetBytes(SearchRequest(destination, searchTarget));
        sockets.Send(request, local => local.AddressFamily == destination.AddressFamily ? destination : null);

        var responses = new List<SsdpResponse>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(timeout);
        await sockets.ReceiveAsync(
            (datagram, sender) =>
            {
                lock (responses)
                {
                    responses.Add(new SsdpResponse(sender, ParseHeaders(Encoding.UTF8.GetString(datagram))));
                }
            },
            window.Token);
        cancellationToken.ThrowIfCancellationRequested();
        return responses;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
