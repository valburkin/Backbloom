using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Overglow.Devices;
using Overglow.Discovery;
using Overglow.Discovery.Mdns;
using Overglow.Discovery.Ssdp;
using Overglow.Discovery.Usb;
using Overglow.Mqtt;
using Overglow.Server;

namespace Overglow.App;

/// <summary>The LED drivers and what they share: HTTP clients, discovery, the MQTT connection.</summary>
internal static class DeviceRegistration
{
    public static IServiceCollection AddDevices(this IServiceCollection services)
    {
        services.AddSingleton(_ => new DriverRegistry().RegisterAll());
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));
        services.AddSingleton(sp => new MdnsService(
            new UdpMdnsTransport(sp.GetRequiredService<ILogger<UdpMdnsTransport>>()), sp.GetRequiredService<ILogger<MdnsService>>()));
        services.AddSingleton(sp => new DiscoveryServices(
            sp.GetRequiredService<MdnsService>(), new SsdpSearcher(), new SerialPortEnumerator(), new HidEnumerator(), new UdpProbe()));
        services.AddSingleton<MqttConnection>();
        services.AddSingleton(sp => new DriverContext(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<HttpClient>())
        {
            LanHttps = CreateLanHttpsClient(),
            Discovery = sp.GetRequiredService<DiscoveryServices>(),
            Mqtt = sp.GetRequiredService<MqttConnection>(),
        });
        services.AddSingleton<DeviceTools>();
        services.AddHostedService<NetworkHostedService>();
        return services;
    }

    /// <summary>
    /// HTTPS to devices on the local network (Hue bridge API v2, Home Assistant with TLS): their certificates are
    /// self-signed or issued for a bridge id, so a certificate that doesn't validate is accepted for private, link-local
    /// and ".local" hosts only (the C++ build ignored the errors for every host).
    /// </summary>
    private static HttpClient CreateLanHttpsClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || IsLocalHost(request.RequestUri?.Host),
    });

    internal static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return false;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || IPAddress.IsLoopback(address);
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }
}

/// <summary>
/// Advertises the web UI with mDNS when the server listens on the network (not when it is bound to loopback only).
/// The MQTT connection is configured by the MQTT integration (<c>Overglow.Server.Mqtt.MqttIntegration</c>).
/// </summary>
internal sealed partial class NetworkHostedService(
    AppOptions options,
    ServerOptions server,
    MdnsService mdns,
    ILogger<NetworkHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IPAddress.IsLoopback(options.BindAddress))
        {
            var addresses = options.BindAddress.Equals(IPAddress.Any) ? [.. UdpMdnsTransport.InterfaceAddresses()] : new List<IPAddress> { options.BindAddress };
            var host = Environment.MachineName;
            mdns.Advertise(new MdnsAdvertisement(KnownServices.WebUi, $"{host}:{options.Port}", $"{host}.local.", options.Port, [$"version={server.Version}"], addresses));
        }
        else
        {
            LogNotAdvertised(options.BindAddress);
        }

        return Task.CompletedTask;
    }

    /// <summary>The container disposes the mDNS service (goodbye) and the MQTT connection (shutdown messages).</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "The web UI isn't advertised with mDNS: the server only listens on {Address}")]
    private partial void LogNotAdvertised(IPAddress address);
}
