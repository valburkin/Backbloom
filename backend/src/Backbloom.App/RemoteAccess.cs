using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Backbloom.Discovery;
using Backbloom.Discovery.Mdns;
using Backbloom.Server;
using Backbloom.Server.Auth;
using Backbloom.Settings;

namespace Backbloom.App;

/// <summary>
/// The web server's endpoints on the local network as Kestrel configuration, one per interface address
/// (<c>Endpoints:Lan-&lt;address&gt;:Url</c>) while remote access is on, none otherwise. Kestrel reloads it when it
/// changes and binds or unbinds just the endpoints that changed, without a restart; the loopback endpoint is
/// configured in code and never touched, so the app's own window stays connected. Per-address endpoints rather than
/// 0.0.0.0, so they never overlap the loopback one, and the tests exercise exactly this with 127.0.0.2.
/// </summary>
public sealed class NetworkEndpointConfiguration
{
    private readonly Provider _provider = new();

    public NetworkEndpointConfiguration() => Configuration = new ConfigurationBuilder().Add(new Source(_provider)).Build();

    /// <summary>What Kestrel reads (with <c>reloadOnChange</c>).</summary>
    public IConfiguration Configuration { get; }

    /// <summary>The network endpoints now (empty: loopback only).</summary>
    public IReadOnlyList<IPEndPoint> Endpoints { get; private set; } = [];

    /// <summary>Replaces the network endpoints (empty: none).</summary>
    public void Set(IReadOnlyList<IPEndPoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.SequenceEqual(Endpoints))
        {
            return;
        }

        Endpoints = [.. endpoints];
        _provider.Set(Endpoints.ToDictionary(
            e => string.Create(CultureInfo.InvariantCulture, $"Endpoints:Lan-{e.Address}:Url"),
            e => (string?)string.Create(CultureInfo.InvariantCulture, $"http://{e}"),
            StringComparer.OrdinalIgnoreCase));
    }

    private sealed class Source(Provider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    private sealed class Provider : ConfigurationProvider
    {
        public void Set(Dictionary<string, string?> data)
        {
            Data = data;
            OnReload();
        }
    }
}

/// <summary>The addresses the web server binds while remote access is on: every IPv4 interface (tests: 127.0.0.2).</summary>
public sealed record RemoteAccessOptions(Func<IReadOnlyList<IPAddress>> NetworkAddresses)
{
    public static RemoteAccessOptions Default { get; } = new(() => [.. LocalNetwork.Addresses().Select(a => a.Address)]);
}

/// <summary>
/// Opt-in network access (<c>network.remoteAccess</c>, off by default and after a HyperHDR import). Off: the server
/// listens on 127.0.0.1 only and nothing is advertised, so Windows never asks for network access. On (after the
/// UI's explanation) and with a password set (other devices couldn't sign in without one; <c>setconfig</c> refuses
/// to turn it on before): an endpoint per interface address is bound and the UI advertised with mDNS (port 5353),
/// both live, and again when the computer's addresses change; off again closes both. <c>--bind</c> overrides the
/// setting with one fixed address.
/// </summary>
internal sealed partial class RemoteAccess(
    AppOptions options,
    RemoteAccessOptions remote,
    ServerOptions server,
    ISettingsStore settings,
    AuthService auth,
    MdnsService mdns,
    NetworkEndpointConfiguration endpoint,
    IServer kestrel,
    IHostApplicationLifetime lifetime,
    ILogger<RemoteAccess> logger) : IHostedService, INetworkAccess
{
    private readonly Lock _lock = new();
    private NetworkAccessStatus _status = new(false, options.BindAddress is not null, options.Port, [], false);
    private bool _running;

    public event Action? Changed;

    public NetworkAccessStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>The <c>network.remoteAccess</c> setting.</summary>
    public static bool IsEnabledIn(JsonNode? network) => network?["remoteAccess"] is JsonValue v && v.TryGetValue(out bool on) && on;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += OnSettingsChanged;
        auth.PasswordChanged += Apply;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;

        // the actual port is known once Kestrel listens (tests use port 0)
        lifetime.ApplicationStarted.Register(() =>
        {
            lock (_lock)
            {
                _running = true;
            }

            Apply();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        auth.PasswordChanged -= Apply;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        lock (_lock)
        {
            _running = false;
        }

        mdns.StopAdvertising();
        return Task.CompletedTask;
    }

    private void OnSettingsChanged(SettingsChange change)
    {
        if (change.Contains("network"))
        {
            Apply();
        }
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        NetworkAccessStatus status;
        bool changed;
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }

            var port = ListeningPort();
            var fixedAddress = options.BindAddress;
            var listensOnNetwork = fixedAddress is null ? IsEnabledIn(settings.GetSection("network")) && auth.HasPassword : !IPAddress.IsLoopback(fixedAddress);
            var addresses = !listensOnNetwork ? []
                : fixedAddress is null || fixedAddress.Equals(IPAddress.Any) ? remote.NetworkAddresses()
                : [fixedAddress];
            if (fixedAddress is null)
            {
                endpoint.Set([.. addresses.Select(a => new IPEndPoint(a, port))]);
                if (listensOnNetwork != _status.Enabled && listensOnNetwork)
                {
                    LogRemoteAccessOn(port, addresses);
                }
                else if (listensOnNetwork != _status.Enabled)
                {
                    LogRemoteAccessOff();
                }
            }

            var advertised = listensOnNetwork && addresses.Count > 0 && Advertise(addresses, port);
            if (!advertised)
            {
                mdns.StopAdvertising();
            }

            status = new NetworkAccessStatus(
                listensOnNetwork,
                fixedAddress is not null,
                port,
                [.. addresses.Select(a => string.Create(CultureInfo.InvariantCulture, $"http://{a}:{port}/"))],
                advertised);
            changed = status.Enabled != _status.Enabled || status.Advertised != _status.Advertised || status.Port != _status.Port ||
                !status.Urls.SequenceEqual(_status.Urls, StringComparer.Ordinal);
            _status = status;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private bool Advertise(IReadOnlyList<IPAddress> addresses, int port)
    {
        var host = Environment.MachineName;
        return mdns.Advertise(new MdnsAdvertisement(KnownServices.WebUi, $"{host}:{port}", $"{host}.local.", port, [$"version={server.Version}"], addresses));
    }

    // the loopback endpoint's port (the configured one, or the one Kestrel picked for port 0)
    private int ListeningPort()
    {
        var addresses = kestrel.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return options.Port;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote access is on: the web UI listens on the local network (port {Port} on {Addresses})")]
    private partial void LogRemoteAccessOn(int port, IReadOnlyList<IPAddress> addresses);

    [LoggerMessage(Level = LogLevel.Information, Message = "Remote access is off: the web UI listens on 127.0.0.1 only")]
    private partial void LogRemoteAccessOff();
}
