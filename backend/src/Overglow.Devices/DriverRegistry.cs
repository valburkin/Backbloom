using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Devices.Transport;
using Overglow.Discovery;
using Overglow.Discovery.Mdns;
using Overglow.Discovery.Ssdp;
using Overglow.Discovery.Usb;
using Overglow.Mqtt;

namespace Overglow.Devices;

/// <summary>Services a driver may use; built once by the composition root and passed to every driver factory.</summary>
public sealed class DriverContext(
    ILoggerFactory loggerFactory,
    HttpClient http,
    Func<IDatagramChannel>? createDatagramChannel = null,
    Func<string, CancellationToken, ValueTask<IPAddress>>? resolveHost = null)
{
    private DiscoveryServices? _discovery;

    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    /// <summary>Shared HTTP client for controllers with a REST API.</summary>
    public HttpClient Http { get; } = http;

    /// <summary>
    /// HTTP client for HTTPS APIs of devices on the local network (Hue bridge API v2, Home Assistant): their
    /// certificates are self-signed or issued for a bridge id, so they aren't validated (as in the C++ code).
    /// </summary>
    public HttpClient LanHttps { get; init; } = http;

    public Func<IDatagramChannel> CreateDatagramChannel { get; } = createDatagramChannel ?? (() => new UdpDatagramChannel());

    public Func<string, CancellationToken, ValueTask<IPAddress>> ResolveHost { get; } = resolveHost ?? ResolveWithDns;

    public ISerialPortFactory SerialPorts { get; init; } = new SystemSerialPortFactory();

    public IHidOpener Hid { get; init; } = new HidSharpOpener();

    /// <summary>The broker connection (Zigbee2MQTT); null when the host has none.</summary>
    public IMqttConnection? Mqtt { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>mDNS, SSDP, serial port and HID enumeration (created on first use unless the host provides them).</summary>
    public DiscoveryServices Discovery
    {
        get => _discovery ??= new DiscoveryServices(
            new MdnsService(new UdpMdnsTransport(LoggerFactory.CreateLogger<UdpMdnsTransport>()), LoggerFactory.CreateLogger<MdnsService>()),
            new SsdpSearcher(),
            new SerialPortEnumerator(),
            new HidEnumerator(),
            new UdpProbe());
        init => _discovery = value;
    }

    private static async ValueTask<IPAddress> ResolveWithDns(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ??
            addresses.FirstOrDefault() ??
            throw new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>Arguments of a driver tool request (<c>leddevice discover / getProperties / identify</c>).</summary>
/// <param name="Parameters">The request's <c>params</c> object.</param>
public sealed record DeviceRequest(JsonObject Parameters, DriverContext Context, CancellationToken CancellationToken);

/// <summary>One driver the backend can run.</summary>
/// <param name="Type">Value of the <c>device.type</c> setting.</param>
/// <param name="Group">UI grouping as reported in <c>serverinfo.ledDevices</c> (e.g. "leds_group_2_network").</param>
/// <param name="Schema">JSON schema of the driver's own settings (merged into the <c>device</c> section).</param>
/// <param name="Create">Builds a driver from the full <c>device</c> settings object.</param>
public sealed record DriverDescriptor(string Type, string Group, JsonObject Schema, Func<JsonObject, DriverContext, ILedDriver> Create)
{
    /// <summary>Finds controllers: returns the list for <c>devices</c>, each with at least <c>value</c> and <c>name</c>.</summary>
    public Func<DeviceRequest, Task<JsonArray>>? Discover { get; init; }

    /// <summary>Reads details from a controller (e.g. a Hue bridge's lights); the object for <c>properties</c>.</summary>
    public Func<DeviceRequest, Task<JsonObject>>? GetProperties { get; init; }

    /// <summary>Makes one light or controller signal itself (blink, pulse).</summary>
    public Func<DeviceRequest, Task>? Identify { get; init; }
}

/// <summary>
/// The drivers available to this process, registered explicitly by the composition root (the C++ code relied on
/// static initializers, which silently disappear when linking static libraries).
/// </summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<string, DriverDescriptor> _drivers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<DriverDescriptor> Drivers => _drivers.Values;

    public void Register(DriverDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_drivers.TryAdd(descriptor.Type, descriptor))
        {
            throw new InvalidOperationException($"LED driver '{descriptor.Type}' is already registered.");
        }
    }

    public bool TryGet(string type, out DriverDescriptor descriptor) => _drivers.TryGetValue(type, out descriptor!);

    /// <summary>Loads a schema embedded in this assembly (Schemas/&lt;name&gt;.json).</summary>
    public static JsonObject LoadEmbeddedSchema(string name)
    {
        var resource = $"Overglow.Devices.Schemas.{name}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource) ??
            throw new InvalidOperationException($"Missing embedded schema {resource}.");
        return JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException($"Empty schema {resource}.");
    }
}
