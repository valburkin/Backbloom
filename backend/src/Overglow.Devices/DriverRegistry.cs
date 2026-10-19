using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Devices.Transport;

namespace Overglow.Devices;

/// <summary>Services a driver may use; built once by the composition root and passed to every driver factory.</summary>
public sealed class DriverContext(
    ILoggerFactory loggerFactory,
    HttpClient http,
    Func<IDatagramChannel>? createDatagramChannel = null,
    Func<string, CancellationToken, ValueTask<IPAddress>>? resolveHost = null)
{
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;

    /// <summary>Shared HTTP client for controllers with a REST API.</summary>
    public HttpClient Http { get; } = http;

    public Func<IDatagramChannel> CreateDatagramChannel { get; } = createDatagramChannel ?? (() => new UdpDatagramChannel());

    public Func<string, CancellationToken, ValueTask<IPAddress>> ResolveHost { get; } = resolveHost ?? ResolveWithDns;

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

/// <summary>One driver the backend can run.</summary>
/// <param name="Type">Value of the <c>device.type</c> setting.</param>
/// <param name="Group">UI grouping as reported in <c>serverinfo.ledDevices</c> (e.g. "leds_group_2_network").</param>
/// <param name="Schema">JSON schema of the driver's own settings (merged into the <c>device</c> section).</param>
/// <param name="Create">Builds a driver from the full <c>device</c> settings object.</param>
public sealed record DriverDescriptor(string Type, string Group, JsonObject Schema, Func<JsonObject, DriverContext, ILedDriver> Create);

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
