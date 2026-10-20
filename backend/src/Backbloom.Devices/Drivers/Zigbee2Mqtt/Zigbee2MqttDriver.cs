using System.Text.Json;
using System.Text.Json.Nodes;
using Backbloom.Devices.Drivers.HomeAssistant;
using Backbloom.Mqtt;

namespace Backbloom.Devices.Drivers.Zigbee2Mqtt;

/// <summary>Settings of the "zigbee2mqtt" driver (sources/led-drivers/schemas/schema-zigbee2mqtt.json).</summary>
public sealed record Zigbee2MqttOptions(IReadOnlyList<LampEntity> Lamps, int TransitionMs = 0, int ConstantBrightness = 255)
{
    public static Zigbee2MqttOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new Zigbee2MqttOptions(
            HomeAssistantOptions.ParseLamps(config),
            DeviceConfig.GetInt(config, "transition", 0),
            DeviceConfig.GetInt(config, "constantBrightness", 255));
    }
}

/// <summary>
/// Zigbee lamps through a Zigbee2MQTT bridge (port of <c>DriverNetZigbee2mqtt</c>,
/// sources/led-drivers/net/DriverNetZigbee2mqtt.cpp): one <c>zigbee2mqtt/&lt;lamp&gt;/set</c> message per lamp and frame
/// over the application's MQTT connection, 30 ms apart; the lamps are switched off when the connection closes.
/// </summary>
public sealed class Zigbee2MqttDriver : ILedDriver
{
    public const string TypeName = "zigbee2mqtt";
    public const string DevicesTopic = "zigbee2mqtt/bridge/devices";

    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(30);
    private static int s_instances;

    private readonly Zigbee2MqttOptions _options;
    private readonly IMqttConnection _mqtt;
    private readonly TimeProvider _time;
    private readonly int[] _currentBrightness;
    private readonly string _owner = $"DriverNetZigbee2mqtt:{Interlocked.Increment(ref s_instances)}";
    private long _lastUpdate;

    public Zigbee2MqttDriver(Zigbee2MqttOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (options.Lamps.Count == 0)
        {
            throw new ArgumentException("No Zigbee2MQTT lamp is configured.", nameof(options));
        }

        _options = options;
        _mqtt = context.Mqtt ?? throw new ArgumentException("Zigbee2MQTT needs the MQTT connection (configure it under Network → MQTT).", nameof(context));
        _time = context.Time;
        _currentBrightness = [.. options.Lamps.Select(_ => options.ConstantBrightness)];
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new Zigbee2MqttDriver(Zigbee2MqttOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
        Identify = IdentifyAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy => RetryPolicy.Default;

    public string? Status => null;

    public static string Topic(string lamp) => $"zigbee2mqtt/{lamp}/set";

    /// <summary>The <c>/set</c> payload for one lamp and color (keys sorted, as QJsonDocument writes them).</summary>
    internal static string ColorMessage(LampEntity lamp, byte r, byte g, byte b, int transitionMs, int? brightness, out int colorBrightness)
    {
        JsonObject color;
        if (lamp.Hsv)
        {
            var (h, s, l) = LampColor.RgbToHsl(r, g, b);
            color = new JsonObject { ["hue"] = h, ["saturation"] = (int)Math.Round(s * 100.0, MidpointRounding.AwayFromZero) };
            colorBrightness = Math.Clamp((int)Math.Round(l * 255.0, MidpointRounding.AwayFromZero), 0, 255);
        }
        else
        {
            color = new JsonObject { ["b"] = b, ["g"] = g, ["r"] = r };
            colorBrightness = LampColor.Luma(r, g, b);
        }

        var row = new JsonObject();
        if (brightness is { } bri)
        {
            row["brightness"] = bri;
        }

        row["color"] = color;
        if (transitionMs > 0)
        {
            row["transition"] = transitionMs / 1000.0;
        }

        return row.ToJsonString(LampColor.QtJson);
    }

    /// <summary>
    /// Lamps from the bridge's retained device list: every device exposing a light with a color_xy (RGB) or color_hs
    /// (HSV) feature; <c>value</c> is the color model, <c>name</c> the friendly name.
    /// </summary>
    public static async Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = new JsonArray();
        if (request.Context.Mqtt is not { IsConnected: true } mqtt)
        {
            return list;
        }

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (await mqtt.SubscribeAsync(DevicesTopic, (_, payload) => received.TrySetResult(payload), request.CancellationToken))
        {
            await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromMilliseconds(1500), request.Context.Time, request.CancellationToken));
        }

        if (!received.Task.IsCompleted)
        {
            return list;
        }

        foreach (var (name, model) in ParseDevices(received.Task.Result))
        {
            list.Add((JsonNode)new JsonObject { ["value"] = model, ["name"] = name });
        }

        return list;
    }

    internal static List<(string Name, string Model)> ParseDevices(string payload)
    {
        var result = new List<(string, string)>();
        JsonNode? doc;
        try
        {
            doc = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return result;
        }

        foreach (var device in (doc as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = DeviceConfig.GetString(device, "friendly_name");
            if (name.Length == 0 || device["definition"] is not JsonObject definition)
            {
                continue;
            }

            foreach (var expose in (definition["exposes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!string.Equals(DeviceConfig.GetString(expose, "type"), "light", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var model = (expose["features"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(f => DeviceConfig.GetString(f, "name").ToUpperInvariant() switch { "COLOR_XY" => "RGB", "COLOR_HS" => "HSV", _ => null })
                    .FirstOrDefault(m => m is not null);
                if (model is not null)
                {
                    result.Add((name, model));
                }
            }
        }

        return result;
    }

    /// <summary>On, red (RGB) or hue 360 (HSV) at full brightness for 0.7 s, off.</summary>
    public static async Task IdentifyAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = DeviceConfig.GetString(request.Parameters, "name");
        var type = DeviceConfig.GetString(request.Parameters, "type");
        if (name.Length == 0 || type.Length == 0 || request.Context.Mqtt is not { } mqtt)
        {
            return;
        }

        var topic = Topic(name);
        var ct = request.CancellationToken;
        await mqtt.PublishAsync(topic, """{"state":"ON"}""", ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), request.Context.Time, ct);
        await mqtt.PublishAsync(topic, type == "RGB" ? """{"brightness":255,"color":{"b":0,"g":0,"r":255}}""" : """{"brightness":255,"color":{"hue":360,"saturation":100}}""", ct);
        await Task.Delay(TimeSpan.FromMilliseconds(700), request.Context.Time, ct);
        await mqtt.PublishAsync(topic, """{"state":"OFF"}""", ct);
    }

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        if (!_mqtt.IsConnected)
        {
            throw new IOException("Not connected to the MQTT broker.");
        }

        await PowerAsync(true, cancellationToken);
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        var n = Math.Min(colors.Length / 3, _options.Lamps.Count);
        var start = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var lastUpdate = _lastUpdate;
        var messages = new (string Topic, string Payload)[n];
        for (var i = 0; i < n; i++)
        {
            var (r, g, b) = (colors[i * 3], colors[(i * 3) + 1], colors[(i * 3) + 2]);
            ColorMessage(_options.Lamps[i], r, g, b, 0, null, out var brightness);
            int? send = null;
            if (_options.ConstantBrightness == 0)
            {
                send = _currentBrightness[i] = brightness;
            }
            else if (_currentBrightness[i] <= 0 && brightness > 0)
            {
                send = _currentBrightness[i] = _options.ConstantBrightness;
            }
            else if (_currentBrightness[i] > 0 && brightness == 0)
            {
                send = _currentBrightness[i] = 0;
            }
            else if (start - lastUpdate >= 10000)
            {
                _lastUpdate = start;
                send = _currentBrightness[i];
            }

            messages[i] = (Topic(_options.Lamps[i].Name), ColorMessage(_options.Lamps[i], r, g, b, _options.TransitionMs, send, out _));
        }

        var ok = true;
        foreach (var (topic, payload) in messages)
        {
            ok &= _mqtt.PublishAsync(topic, payload, CancellationToken.None).GetAwaiter().GetResult();
            Thread.Sleep(s_pause);
        }

        return ok;
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken) => await PowerAsync(false, cancellationToken);

    public void Dispose() => _mqtt.SetShutdownMessages(_owner, null);

    private async Task PowerAsync(bool on, CancellationToken cancellationToken)
    {
        foreach (var lamp in _options.Lamps)
        {
            await _mqtt.PublishAsync(Topic(lamp.Name), on ? """{"state":"ON"}""" : """{"state":"OFF"}""", cancellationToken);
        }

        _mqtt.SetShutdownMessages(_owner, on ? [.. _options.Lamps.Select(l => new KeyValuePair<string, string>(Topic(l.Name), """{"state":"OFF"}"""))] : null);
        _lastUpdate = _time.GetUtcNow().ToUnixTimeMilliseconds() - 9000;
    }
}
