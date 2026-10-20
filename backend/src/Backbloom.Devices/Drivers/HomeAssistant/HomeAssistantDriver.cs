using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Discovery;

namespace Backbloom.Devices.Drivers.HomeAssistant;

/// <summary>One light entity and how its color is sent (0 = RGB, 1 = hue/saturation).</summary>
public sealed record LampEntity(string Name, bool Hsv);

/// <summary>Settings of the "home_assistant" driver (sources/led-drivers/schemas/schema-home_assistant.json).</summary>
/// <param name="ConstantBrightness">0 sends the color's brightness with every frame; otherwise this value, on change only.</param>
public sealed record HomeAssistantOptions(
    string Host,
    string Token,
    IReadOnlyList<LampEntity> Lamps,
    int TransitionMs = 0,
    int ConstantBrightness = 255,
    bool RestoreOriginalState = false,
    int MaxRetry = 60)
{
    public static HomeAssistantOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new HomeAssistantOptions(
            DeviceConfig.GetString(config, "homeAssistantHost"),
            DeviceConfig.GetString(config, "longLivedAccessToken"),
            ParseLamps(config),
            DeviceConfig.GetInt(config, "transition", 0),
            DeviceConfig.GetInt(config, "constantBrightness", 255),
            DeviceConfig.GetBool(config, "restoreOriginalState", false),
            DeviceConfig.GetInt(config, "maxRetry", 60));
    }

    internal static List<LampEntity> ParseLamps(JsonObject config) =>
        [.. DeviceConfig.GetObjects(config, "lamps").Select(l => new LampEntity(DeviceConfig.GetString(l, "name"), DeviceConfig.GetInt(l, "colorModel", 0) == 1))];

    /// <summary>The API base: "http://host:8123" unless the setting has a scheme or a port.</summary>
    public Uri BaseUri
    {
        get
        {
            var text = Host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? Host : "http://" + Host;
            var uri = new Uri(text);
            return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? 8123 : uri.Port).Uri;
        }
    }
}

/// <summary>
/// Home Assistant light entities over its REST API (port of <c>DriverNetHomeAssistant</c>,
/// sources/led-drivers/net/DriverNetHomeAssistant.cpp): <c>light.turn_on</c> with an RGB or hue/saturation color per
/// lamp and frame, the brightness only when it changes (and every 10 s), optionally restoring the lamps' state.
/// </summary>
public sealed partial class HomeAssistantDriver : ILedDriver
{
    public const string TypeName = "home_assistant";

    private readonly HomeAssistantOptions _options;
    private readonly JsonRest _rest;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int[] _currentBrightness;
    private readonly SavedState?[] _saved;
    private long _lastUpdate;

    public HomeAssistantDriver(HomeAssistantOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (options.Host.Length == 0 || options.Token.Length == 0 || options.Lamps.Count == 0)
        {
            throw new ArgumentException("The Home Assistant address, access token and lamps must be configured.", nameof(options));
        }

        _options = options;
        var http = options.BaseUri.Scheme == Uri.UriSchemeHttps ? context.LanHttps : context.Http;
        _rest = new JsonRest(http, new Dictionary<string, string> { ["Authorization"] = $"Bearer {options.Token}" });
        _time = context.Time;
        _logger = context.LoggerFactory.CreateLogger<HomeAssistantDriver>();
        _currentBrightness = [.. options.Lamps.Select(_ => options.ConstantBrightness)];
        _saved = new SavedState?[options.Lamps.Count];
        RetryPolicy = new RetryPolicy(Math.Max(0, options.MaxRetry), TimeSpan.FromSeconds(5));
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new HomeAssistantDriver(HomeAssistantOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy { get; }

    public string? Status => null;

    /// <summary>Home Assistant servers announced with mDNS (<c>_home-assistant._tcp</c>), as "ip:8123".</summary>
    public static async Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = new JsonArray();
        foreach (var s in await request.Context.Discovery.Mdns.BrowseAsync(KnownServices.HomeAssistant, TimeSpan.FromSeconds(2), request.CancellationToken))
        {
            var value = $"{s.Address}:8123";
            list.Add((JsonNode)new JsonObject { ["value"] = value, ["name"] = $"{value} ({s.HostName})" });
        }

        return list;
    }

    /// <summary>The <c>light.turn_on</c> body for one lamp and color (keys sorted, as QJsonDocument writes them).</summary>
    internal static string ColorRequest(LampEntity lamp, byte r, byte g, byte b, int transitionMs, int? brightness, out int colorBrightness)
    {
        var row = new JsonObject();
        JsonNode color;
        if (lamp.Hsv)
        {
            var (h, s, l) = LampColor.RgbToHsl(r, g, b);
            color = new JsonArray(h, (int)Math.Round(s * 100.0, MidpointRounding.AwayFromZero));
            colorBrightness = Math.Clamp((int)Math.Round(l * 255.0, MidpointRounding.AwayFromZero), 0, 255);
        }
        else
        {
            color = new JsonArray(r, g, b);
            colorBrightness = LampColor.Luma(r, g, b);
        }

        if (brightness is { } bri)
        {
            row["brightness"] = bri;
        }

        row["entity_id"] = lamp.Name;
        row[lamp.Hsv ? "hs_color" : "rgb_color"] = color;
        if (transitionMs > 0)
        {
            row["transition"] = transitionMs / 1000.0;
        }

        return row.ToJsonString(LampColor.QtJson);
    }

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        if (_options.RestoreOriginalState)
        {
            await SaveStatesAsync(cancellationToken);
        }

        _lastUpdate = NowMs() - 9000;
        await PowerAsync(true, cancellationToken);
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        var n = Math.Min(colors.Length / 3, _options.Lamps.Count);
        var requests = new string[n];
        var start = NowMs();
        var lastUpdate = _lastUpdate;
        for (var i = 0; i < n; i++)
        {
            ColorRequest(_options.Lamps[i], colors[i * 3], colors[(i * 3) + 1], colors[(i * 3) + 2], 0, null, out var brightness);
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

            requests[i] = ColorRequest(_options.Lamps[i], colors[i * 3], colors[(i * 3) + 1], colors[(i * 3) + 2], _options.TransitionMs, send, out _);
        }

        foreach (var body in requests)
        {
            var reply = _rest.PostAsync(Service("turn_on"), body, CancellationToken.None).GetAwaiter().GetResult();
            if (!reply.IsSuccess)
            {
                throw new DeviceLostException($"Home Assistant answered HTTP {reply.Status}.");
            }
        }

        return true;
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_options.RestoreOriginalState)
        {
            await RestoreStatesAsync(cancellationToken);
        }
        else
        {
            await PowerAsync(false, cancellationToken);
        }
    }

    public void Dispose()
    {
    }

    private long NowMs() => (long)_time.GetUtcNow().ToUnixTimeMilliseconds();

    private Uri Service(string service) => new(_options.BaseUri, $"/api/services/light/{service}");

    private async Task PowerAsync(bool on, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["entity_id"] = new JsonArray([.. _options.Lamps.Select(l => (JsonNode)JsonValue.Create(l.Name))]) };
        var reply = await _rest.PostAsync(Service(on ? "turn_on" : "turn_off"), body.ToJsonString(LampColor.QtJson), cancellationToken);
        if (!reply.IsSuccess)
        {
            throw new IOException($"Home Assistant answered HTTP {reply.Status}.");
        }
    }

    private async Task SaveStatesAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < _options.Lamps.Count; i++)
        {
            var lamp = _options.Lamps[i];
            var reply = await _rest.GetAsync(new Uri(_options.BaseUri, $"/api/states/{lamp.Name}"), cancellationToken);
            if (!reply.IsSuccess)
            {
                throw new IOException($"Home Assistant answered HTTP {reply.Status}.");
            }

            if (reply.Body is not JsonObject state)
            {
                LogUnknownState(lamp.Name);
                continue;
            }

            int poweredOn = state["state"] is JsonValue v && v.TryGetValue(out string? s) ? (string.Equals(s, "on", StringComparison.OrdinalIgnoreCase) ? 1 : 0) : -1;
            var attributes = state["attributes"] as JsonObject;
            var brightness = attributes is not null && attributes["brightness"] is JsonValue bv && bv.TryGetValue(out double bd) ? (int)bd : -1;
            var color = attributes?[lamp.Hsv ? "hs_color" : "rgb_color"] as JsonArray;
            _saved[i] = new SavedState(poweredOn, brightness, color?.DeepClone().AsArray());
            LogSaved(lamp.Name, poweredOn, brightness);
        }
    }

    private async Task RestoreStatesAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < _options.Lamps.Count; i++)
        {
            if (_saved[i] is not { PoweredOn: >= 0 } saved)
            {
                continue;
            }

            var lamp = _options.Lamps[i];
            var row = new JsonObject();
            if (saved.Brightness >= 0)
            {
                row["brightness"] = saved.Brightness;
            }

            row["entity_id"] = lamp.Name;
            if (saved.Color is { Count: > 0 } color)
            {
                row[lamp.Hsv ? "hs_color" : "rgb_color"] = color.DeepClone();
            }

            await _rest.PostAsync(Service(saved.PoweredOn == 1 ? "turn_on" : "turn_off"), row.ToJsonString(LampColor.QtJson), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The current state of the light {Name} is unknown")]
    private partial void LogUnknownState(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved the state of {Name}: on {PoweredOn}, brightness {Brightness}")]
    private partial void LogSaved(string name, int poweredOn, int brightness);

    private sealed record SavedState(int PoweredOn, int Brightness, JsonArray? Color);
}
