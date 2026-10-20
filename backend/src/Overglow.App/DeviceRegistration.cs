using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Overglow.Devices;
using Overglow.Discovery;
using Overglow.Discovery.Mdns;
using Overglow.Discovery.Ssdp;
using Overglow.Discovery.Usb;
using Overglow.Mqtt;

namespace Overglow.App;

/// <summary>The LED drivers and what they share: HTTP clients, discovery, the MQTT connection.</summary>
internal static class DeviceRegistration
{
    public static IServiceCollection AddDevices(this IServiceCollection services)
    {
        services.AddSingleton(_ => new DriverRegistry().RegisterAll());
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }));
        // Browsing opens ephemeral sockets per query; the 5353 responder only exists while remote access advertises the UI.
        services.AddSingleton(sp => MdnsService.Create(sp.GetRequiredService<ILoggerFactory>()));
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
