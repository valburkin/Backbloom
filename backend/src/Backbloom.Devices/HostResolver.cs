using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Backbloom.Discovery.Mdns;

namespace Backbloom.Devices;

/// <summary>
/// Finds the address of a controller's host name: with the system resolver, for a ".local" name that it can't resolve
/// with an mDNS query of our own, and when both fail with the last address the name had. Not in the C++ code: Windows'
/// resolver once kept failing for a WLED controller's ".local" name in the running process (HyperHDR and Backbloom
/// alike) while a browser and a restarted process resolved it at once, so a controller must not depend on it alone.
/// </summary>
public sealed partial class HostResolver
{
    private static readonly TimeSpan s_mdnsWait = TimeSpan.FromSeconds(1.5);

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _system;
    private readonly TimeSpan _systemTimeout;
    private readonly Func<IMdnsBrowser> _mdns;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IPAddress> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="system">The system resolver (tests); <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> by default.</param>
    /// <param name="systemTimeout">How long the system resolver may take (3 s by default).</param>
    public HostResolver(Func<IMdnsBrowser> mdns, ILogger<HostResolver> logger, Func<string, CancellationToken, Task<IPAddress[]>>? system = null, TimeSpan? systemTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(mdns);
        ArgumentNullException.ThrowIfNull(logger);
        _mdns = mdns;
        _logger = logger;
        _system = system ?? Dns.GetHostAddressesAsync;
        _systemTimeout = systemTimeout ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>The address of <paramref name="host"/> (an IP address is returned as it is), preferring IPv4.</summary>
    /// <exception cref="SocketException">No address is known for the name.</exception>
    public async ValueTask<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        string reason;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_systemTimeout);
            // WaitAsync: the limit holds even for a resolver that doesn't honor the token
            var addresses = await _system(host, timeout.Token).WaitAsync(timeout.Token);
            if ((addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()) is { } found)
            {
                return Remember(host, found);
            }

            reason = "no address";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reason = $"no answer within {_systemTimeout.TotalSeconds:0.#} s";
        }
        catch (SocketException ex)
        {
            reason = ex.Message;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (await _mdns().ResolveAsync(host, s_mdnsWait, cancellationToken) is { } found)
                {
                    LogResolvedWithMdns(host, reason, found);
                    return Remember(host, found);
                }
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                // no network to ask on: the last known address may still do
            }
        }

        if (_known.TryGetValue(host, out var last))
        {
            LogUsingLastAddress(host, reason, last);
            return last;
        }

        throw new SocketException((int)SocketError.HostNotFound, $"Couldn't find the address of {host} ({reason})");
    }

    private IPAddress Remember(string host, IPAddress address)
    {
        _known[host] = address;
        return address;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Host}: the system couldn't resolve the name ({Reason}), mDNS found {Address}")]
    private partial void LogResolvedWithMdns(string host, string reason, IPAddress address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Host}: couldn't resolve the name ({Reason}), trying its last address {Address}")]
    private partial void LogUsingLastAddress(string host, string reason, IPAddress address);
}
