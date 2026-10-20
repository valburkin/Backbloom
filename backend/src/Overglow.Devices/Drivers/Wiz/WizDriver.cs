using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Overglow.Devices.Transport;

namespace Overglow.Devices.Drivers.Wiz;

/// <summary>One configured lamp (WiZ, LIFX).</summary>
public sealed record NetworkLamp(string Name, IPAddress Address, string MacAddress);

/// <summary>Settings of the "wiz" driver (sources/led-drivers/schemas/schema-wiz.json).</summary>
/// <param name="Dimming">Brightness in percent sent with every color.</param>
public sealed record WizOptions(IReadOnlyList<NetworkLamp> Lamps, int Port = WizDriver.DefaultPort, int Dimming = 100)
{
    public static WizOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var lamps = new List<NetworkLamp>();
        foreach (var lamp in DeviceConfig.GetObjects(config, "lamps"))
        {
            if (IPAddress.TryParse(DeviceConfig.GetString(lamp, "ipAddress"), out var address))
            {
                lamps.Add(new NetworkLamp(DeviceConfig.GetString(lamp, "name"), address, DeviceConfig.GetString(lamp, "macAddress")));
            }
        }

        return new WizOptions(lamps, DeviceConfig.GetInt(config, "port", WizDriver.DefaultPort), Math.Clamp(DeviceConfig.GetInt(config, "dimming", 100), 0, 100));
    }
}

/// <summary>
/// WiZ lamps: one JSON "setPilot" datagram per lamp and frame (port of <c>DriverNetWiz</c>,
/// sources/led-drivers/net/DriverNetWiz.cpp). The JSON is written with sorted keys like QJsonDocument does.
/// </summary>
public sealed class WizDriver : ILedDriver
{
    public const string TypeName = "wiz";
    public const int DefaultPort = 38899;

    private readonly WizOptions _options;
    private readonly IDatagramChannel[] _channels;
    private bool _connected;

    public WizDriver(WizOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (options.Lamps.Count == 0)
        {
            throw new ArgumentException("No WiZ lamp with a valid IP address is configured.", nameof(options));
        }

        _options = options;
        _channels = [.. options.Lamps.Select(_ => context.CreateDatagramChannel())];
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new WizDriver(WizOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
        Identify = IdentifyAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy => RetryPolicy.Default;

    public string? Status => null;

    public static string SetPilot(int r, int g, int b, int dimming) =>
        string.Create(CultureInfo.InvariantCulture, $$$"""{"method":"setPilot","params":{"b":{{{b}}},"dimming":{{{dimming}}},"g":{{{g}}},"r":{{{r}}}}}""");

    public static string Power(bool on, int dimming) => on
        ? string.Create(CultureInfo.InvariantCulture, $$$"""{"method":"setPilot","params":{"dimming":{{{dimming}}},"state":true}}""")
        : """{"method":"setPilot","params":{"state":false}}""";

    /// <summary>Broadcasts "getPilot" and lists every lamp that answers within 2 s (one entry per IP / MAC).</summary>
    public static async Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var replies = await request.Context.Discovery.Broadcast.ProbeAsync(
            Encoding.UTF8.GetBytes("""{"method":"getPilot","params":{}}"""), DefaultPort, TimeSpan.FromSeconds(2), request.CancellationToken);
        var lamps = new JsonArray();
        var seen = new List<(string Ip, string Mac)>();
        foreach (var reply in replies)
        {
            string mac;
            try
            {
                mac = JsonNode.Parse(reply.Data) is JsonObject o && o["result"] is JsonObject r ? DeviceConfig.GetString(r, "mac") : string.Empty;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            var ip = reply.From.Address.ToString();
            if (seen.Any(s => s.Ip == ip || (mac.Length > 0 && s.Mac == mac)))
            {
                continue;
            }

            seen.Add((ip, mac));
            lamps.Add((JsonNode)new JsonObject
            {
                ["name"] = $"Bulb #{lamps.Count}",
                ["ipAddress"] = ip,
                ["macAddress"] = mac,
                ["value"] = ip,
            });
        }

        return lamps;
    }

    /// <summary>Blue at full brightness, then the "pulse" effect (port of <c>DriverNetWiz::identify</c>).</summary>
    public static async Task IdentifyAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = request.Parameters;
        if (p["name"] is null || !IPAddress.TryParse(DeviceConfig.GetString(p, "ipAddress"), out var address))
        {
            return;
        }

        using var channel = request.Context.CreateDatagramChannel();
        channel.Connect(new IPEndPoint(address, DeviceConfig.GetInt(p, "port", DefaultPort)));
        channel.Send(Encoding.UTF8.GetBytes("""{"method":"setPilot","params":{"b":255,"dimming":0,"g":0,"r":0,"state":true}}"""));
        await Task.Delay(TimeSpan.FromMilliseconds(400), request.Context.Time, request.CancellationToken);
        channel.Send(Encoding.UTF8.GetBytes("""{"method":"pulse","params":{"delta":100,"duration":1000}}"""));
    }

    public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _channels.Length; i++)
        {
            _channels[i].Connect(new IPEndPoint(_options.Lamps[i].Address, _options.Port));
        }

        _connected = true;
        SendAll(Power(true, _options.Dimming));
        return ValueTask.CompletedTask;
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        if (!_connected)
        {
            return false;
        }

        var ok = true;
        var n = Math.Min(colors.Length / 3, _channels.Length);
        for (var i = 0; i < n; i++)
        {
            ok &= _channels[i].Send(Encoding.UTF8.GetBytes(SetPilot(colors[i * 3], colors[(i * 3) + 1], colors[(i * 3) + 2], _options.Dimming)));
        }

        return ok;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            SendAll(Power(false, _options.Dimming));
        }

        _connected = false;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }

    private void SendAll(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var channel in _channels)
        {
            channel.Send(bytes);
        }
    }
}
