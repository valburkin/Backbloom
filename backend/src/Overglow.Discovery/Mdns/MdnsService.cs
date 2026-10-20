using System.Net;
using Microsoft.Extensions.Logging;

namespace Overglow.Discovery.Mdns;

/// <summary>A service instance found with mDNS / DNS-SD.</summary>
/// <param name="ServiceType">E.g. "_wled._tcp.local.".</param>
/// <param name="InstanceName">Full instance name, e.g. "wled-kitchen._wled._tcp.local.".</param>
/// <param name="HostName">Target host of the SRV record without the trailing dot, e.g. "wled-kitchen.local".</param>
/// <param name="Address">IPv4 address of the host (from its A record, else the address that answered).</param>
public sealed record MdnsServiceInstance(string ServiceType, string InstanceName, string HostName, IPAddress Address, int Port, IReadOnlyList<string> Txt);

/// <summary>A service this process announces (the web UI, so phones can find it).</summary>
/// <param name="ServiceType">E.g. "_overglow-http._tcp.local.".</param>
/// <param name="InstanceLabel">First label of the instance name.</param>
/// <param name="HostName">E.g. "MY-PC.local.".</param>
public sealed record MdnsAdvertisement(string ServiceType, string InstanceLabel, string HostName, int Port, IReadOnlyList<string> Txt, IReadOnlyList<IPAddress> Addresses)
{
    public string InstanceName => $"{InstanceLabel}.{ServiceType.TrimEnd('.')}.";
}

/// <summary>Browses for services and answers queries for the advertised one.</summary>
public interface IMdnsBrowser
{
    /// <summary>
    /// Asks the network for instances of <paramref name="serviceType"/> (a PTR query) and returns, after
    /// <paramref name="wait"/>, every instance known with its host and address. Passive: it only queries and listens.
    /// </summary>
    Task<IReadOnlyList<MdnsServiceInstance>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal mDNS / DNS-SD client and responder (port of the browsing and the web UI announcement of
/// sources/bonjour/BonjourServiceHelper.cpp and DiscoveryWrapper.cpp, which used the mdns.h library). It keeps a cache
/// of every PTR/SRV/TXT/A record it hears (with their TTLs; a TTL of 0 removes a record). Own addresses are never
/// reported as devices.
/// </summary>
/// <remarks>
/// Nothing listens while nothing is asked for: each browse opens its own query transport (ephemeral sockets, see
/// <see cref="UdpMdnsQueryTransport"/>) and closes it afterwards, and the responder transport (port 5353) exists only
/// between <see cref="Advertise"/> and <see cref="StopAdvertising"/>. HyperHDR kept a 5353 listener open all the
/// time, which is what makes Windows Firewall ask.
/// </remarks>
public sealed partial class MdnsService : IMdnsBrowser, IDisposable
{
    public const string ServicesMetaQuery = "_services._dns-sd._udp.local.";

    private const uint HostTtl = 120;
    private const uint ServiceTtl = 4500;

    private readonly Func<IMdnsTransport> _responderFactory;
    private readonly Func<IMdnsTransport> _queryFactory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<IPAddress, bool> _isOwnAddress;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _pointers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SrvData Srv, IPAddress From, DateTimeOffset Expires)> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (IPAddress Address, DateTimeOffset Expires)> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private MdnsAdvertisement? _advertisement;
    private IMdnsTransport? _responder;

    /// <param name="responder">Creates the responder transport (port 5353) when advertising starts.</param>
    /// <param name="queries">Creates the transport of one browse (disposed when the browse ends).</param>
    public MdnsService(Func<IMdnsTransport> responder, Func<IMdnsTransport> queries, ILogger<MdnsService> logger, TimeProvider? time = null, Func<IPAddress, bool>? isOwnAddress = null)
    {
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(logger);
        _responderFactory = responder;
        _queryFactory = queries;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _isOwnAddress = isOwnAddress ?? IsOwnAddress;
    }

    /// <summary>The real transports: ephemeral query sockets per browse, the 5353 responder only while advertising.</summary>
    public static MdnsService Create(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new MdnsService(
            () => new UdpMdnsTransport(loggerFactory.CreateLogger<UdpMdnsTransport>()),
            () => new UdpMdnsQueryTransport(),
            loggerFactory.CreateLogger<MdnsService>());
    }

    public async Task<IReadOnlyList<MdnsServiceInstance>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        var type = Fqdn(serviceType);
        using var transport = _queryFactory();
        transport.Start(Handle);
        var query = DnsMessage.Query(new DnsQuestion(type, DnsType.NamePointer, UnicastResponse: true)).Encode();
        transport.SendMulticast(query);
        if (wait > TimeSpan.FromSeconds(1))
        {
            // mDNS answers can get lost; ask once more halfway (RFC 6762 5.2 asks for repeated queries).
            await Task.Delay(wait / 2, _time, cancellationToken);
            transport.SendMulticast(query);
            await Task.Delay(wait - (wait / 2), _time, cancellationToken);
        }
        else
        {
            await Task.Delay(wait, _time, cancellationToken);
        }

        var unresolved = new List<string>();
        var found = Instances(type, unresolved);
        if (unresolved.Count > 0)
        {
            // Some responders only send the SRV record: ask for the missing host addresses.
            transport.SendMulticast(DnsMessage.Query([.. unresolved.Select(h => new DnsQuestion(h, DnsType.A, UnicastResponse: true))]).Encode());
            await Task.Delay(TimeSpan.FromMilliseconds(300), _time, cancellationToken);
            found = Instances(type, []);
        }

        return found;
    }

    /// <summary>True while a service is advertised (the responder listens on port 5353).</summary>
    public bool IsAdvertising
    {
        get
        {
            lock (_lock)
            {
                return _advertisement is not null;
            }
        }
    }

    /// <summary>
    /// Opens the responder (port 5353), announces <paramref name="advertisement"/> and answers queries for it until
    /// <see cref="StopAdvertising"/>. A second call replaces the advertisement.
    /// </summary>
    /// <returns>False when the mDNS port can't be shared (nothing is advertised).</returns>
    public bool Advertise(MdnsAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        IMdnsTransport responder;
        lock (_lock)
        {
            if (_responder is null)
            {
                var created = _responderFactory();
                created.Start(Handle);
                if (!created.CanAdvertise)
                {
                    created.Dispose();
                    return false;
                }

                _responder = created;
            }

            responder = _responder;
            _advertisement = advertisement;
        }

        var announcement = Announcement(advertisement, HostTtl, ServiceTtl).Encode();
        SendQuietly(responder, announcement);
        _ = Task.Delay(TimeSpan.FromSeconds(1), _time).ContinueWith(_ => SendQuietly(responder, announcement), TaskScheduler.Default);
        LogAdvertising(advertisement.InstanceName, advertisement.Port);
        return true;
    }

    /// <summary>Says goodbye (TTL 0) for the advertised service and closes the responder.</summary>
    public void StopAdvertising()
    {
        MdnsAdvertisement? advertisement;
        IMdnsTransport? responder;
        lock (_lock)
        {
            advertisement = _advertisement;
            responder = _responder;
            _advertisement = null;
            _responder = null;
        }

        if (responder is null)
        {
            return;
        }

        if (advertisement is not null)
        {
            SendQuietly(responder, Announcement(advertisement, 0, 0).Encode());
            LogStopped(advertisement.InstanceName);
        }

        responder.Dispose();
    }

    public void Dispose() => StopAdvertising();

    /// <summary>Processes one received datagram (internal for the protocol tests).</summary>
    internal void Handle(byte[] datagram, IPEndPoint from)
    {
        DnsMessage message;
        try
        {
            message = DnsMessage.Decode(datagram);
        }
        catch (InvalidDataException)
        {
            return;
        }

        if (message.IsResponse)
        {
            Cache(message, from.Address);
        }
        else
        {
            Answer(message, from);
        }
    }

    private static string Fqdn(string name) => name.EndsWith('.') ? name : name + ".";

    private static bool IsOwnAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) || LocalNetwork.Addresses().Any(a => a.Address.Equals(address));

    private static DnsMessage Announcement(MdnsAdvertisement ad, uint hostTtl, uint serviceTtl) => new()
    {
        IsResponse = true,
        Answers =
        [
            new DnsRecord(Fqdn(ad.ServiceType), DnsType.NamePointer, serviceTtl, ad.InstanceName),
            new DnsRecord(ad.InstanceName, DnsType.Srv, hostTtl, new SrvData(0, 0, (ushort)ad.Port, Fqdn(ad.HostName)), CacheFlush: true),
            new DnsRecord(ad.InstanceName, DnsType.Txt, serviceTtl, ad.Txt, CacheFlush: true),
            .. ad.Addresses.Select(a => new DnsRecord(Fqdn(ad.HostName), DnsType.A, hostTtl, a, CacheFlush: true)),
        ],
    };

    private static void SendQuietly(IMdnsTransport transport, byte[] message)
    {
        try
        {
            transport.SendMulticast(message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private List<MdnsServiceInstance> Instances(string type, List<string> unresolvedHosts)
    {
        var now = _time.GetUtcNow();
        var result = new List<MdnsServiceInstance>();
        lock (_lock)
        {
            if (!_pointers.TryGetValue(type, out var instances))
            {
                return result;
            }

            foreach (var (instance, expires) in instances)
            {
                if (expires <= now || !_services.TryGetValue(instance, out var srv) || srv.Expires <= now)
                {
                    continue;
                }

                var address = _hosts.TryGetValue(srv.Srv.Target, out var host) && host.Expires > now ? host.Address : null;
                if (address is null)
                {
                    unresolvedHosts.Add(srv.Srv.Target);
                    address = srv.From;
                }

                if (_isOwnAddress(address))
                {
                    continue;
                }

                result.Add(new MdnsServiceInstance(type, instance, srv.Srv.Target.TrimEnd('.'), address, srv.Srv.Port, _texts.GetValueOrDefault(instance) ?? []));
            }
        }

        return [.. result.OrderBy(r => r.InstanceName, StringComparer.OrdinalIgnoreCase)];
    }

    private void Cache(DnsMessage message, IPAddress from)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var record in message.AllRecords)
            {
                var expires = now + TimeSpan.FromSeconds(record.Ttl);
                switch (record.Data)
                {
                    case string target when record.Type == DnsType.NamePointer:
                        if (!_pointers.TryGetValue(record.Name, out var instances))
                        {
                            instances = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
                            _pointers[record.Name] = instances;
                        }

                        if (record.Ttl == 0)
                        {
                            instances.Remove(target);
                        }
                        else
                        {
                            instances[target] = expires;
                        }

                        break;

                    case SrvData srv:
                        if (record.Ttl == 0)
                        {
                            _services.Remove(record.Name);
                        }
                        else
                        {
                            _services[record.Name] = (srv, from, expires);
                        }

                        break;

                    case IReadOnlyList<string> txt when record.Type == DnsType.Txt:
                        _texts[record.Name] = txt;
                        break;

                    case IPAddress address when record.Type == DnsType.A:
                        if (record.Ttl == 0)
                        {
                            _hosts.Remove(record.Name);
                        }
                        else
                        {
                            _hosts[record.Name] = (address, expires);
                        }

                        break;
                }
            }
        }
    }

    private void Answer(DnsMessage query, IPEndPoint from)
    {
        MdnsAdvertisement? ad;
        IMdnsTransport? responder;
        lock (_lock)
        {
            ad = _advertisement;
            responder = _responder;
        }

        if (ad is null || responder is null)
        {
            return;
        }

        foreach (var q in query.Questions)
        {
            var response = Respond(ad, q);
            if (response is null)
            {
                continue;
            }

            var bytes = response.Encode();
            // Legacy unicast queriers (not sending from port 5353) and the QU bit get a direct answer.
            if (q.UnicastResponse || from.Port != UdpMdnsTransport.MdnsPort)
            {
                responder.SendUnicast(from.Port != UdpMdnsTransport.MdnsPort ? new DnsMessage
                {
                    Id = query.Id,
                    IsResponse = true,
                    Questions = [q],
                    Answers = response.Answers,
                    Additionals = response.Additionals,
                }.Encode() : bytes, from);
            }
            else
            {
                SendQuietly(responder, bytes);
            }
        }
    }

    /// <summary>The answer to one question about the advertised service, or null when it isn't about it.</summary>
    internal static DnsMessage? Respond(MdnsAdvertisement ad, DnsQuestion q)
    {
        var type = Fqdn(ad.ServiceType);
        var host = Fqdn(ad.HostName);
        var ptr = new DnsRecord(type, DnsType.NamePointer, ServiceTtl, ad.InstanceName);
        var srv = new DnsRecord(ad.InstanceName, DnsType.Srv, HostTtl, new SrvData(0, 0, (ushort)ad.Port, host), CacheFlush: true);
        var txt = new DnsRecord(ad.InstanceName, DnsType.Txt, ServiceTtl, ad.Txt, CacheFlush: true);
        DnsRecord[] addresses = [.. ad.Addresses.Select(a => new DnsRecord(host, DnsType.A, HostTtl, a, CacheFlush: true))];
        var wantsPtr = q.Type is DnsType.NamePointer or DnsType.Any;

        if (wantsPtr && DnsMessage.NameEquals(q.Name, ServicesMetaQuery))
        {
            return new DnsMessage { IsResponse = true, Answers = [new DnsRecord(ServicesMetaQuery, DnsType.NamePointer, ServiceTtl, type)] };
        }

        if (wantsPtr && DnsMessage.NameEquals(q.Name, type))
        {
            return new DnsMessage { IsResponse = true, Answers = [ptr], Additionals = [srv, txt, .. addresses] };
        }

        if (DnsMessage.NameEquals(q.Name, ad.InstanceName) && q.Type is DnsType.Srv or DnsType.Txt or DnsType.Any)
        {
            return q.Type == DnsType.Txt
                ? new DnsMessage { IsResponse = true, Answers = [txt] }
                : new DnsMessage { IsResponse = true, Answers = [srv], Additionals = [txt, .. addresses] };
        }

        if (DnsMessage.NameEquals(q.Name, host) && q.Type is DnsType.A or DnsType.Any && addresses.Length > 0)
        {
            return new DnsMessage { IsResponse = true, Answers = addresses };
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Advertising the web UI as {Instance} (port {Port}) with mDNS")]
    private partial void LogAdvertising(string instance, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped advertising {Instance} with mDNS")]
    private partial void LogStopped(string instance);
}
