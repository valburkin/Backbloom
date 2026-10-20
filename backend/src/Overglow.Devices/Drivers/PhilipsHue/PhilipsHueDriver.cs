using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Core.Color;
using Overglow.Devices.Transport;
using Overglow.Discovery;

namespace Overglow.Devices.Drivers.PhilipsHue;

/// <summary>Settings of the "philipshue" driver (sources/led-drivers/schemas/schema-philipshue.json; defaults as the C++ code reads them).</summary>
public sealed record HueOptions(
    string Address,
    string Username,
    string ClientKey = "",
    bool Entertainment = false,
    bool ApiV2 = false,
    bool ConvertToLinearRgb = false,
    bool SwitchOffOnBlack = true,
    float BrightnessFactor = 1.0f,
    int TransitionTime = 1,
    bool RestoreOriginalState = true,
    string EntertainmentConfigurationId = "",
    int GroupId = 0,
    float BlackLevel = 0.0f,
    int OnBlackTimeToPowerOffMs = 100,
    int OnBlackTimeToPowerOnMs = 100,
    int HandshakeTimeoutMinMs = 300,
    int HandshakeTimeoutMaxMs = 1000,
    int MaxRetry = 60,
    IReadOnlyList<int>? LightIds = null,
    ColorOrder ColorOrder = ColorOrder.Rgb,
    int StreamPort = HueProtocol.StreamPort)
{
    public static HueOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var ids = DeviceConfig.GetArray(config, "lightIds")
            .Select(n => n is JsonValue v ? (v.TryGetValue(out string? s) ? s : v.ToString()) : string.Empty)
            .Select(s => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? (int)id : 0)
            .Where(id => id > 0)
            .ToList();
        return new HueOptions(
            DeviceConfig.GetString(config, "output"),
            DeviceConfig.GetString(config, "username"),
            DeviceConfig.GetString(config, "clientkey"),
            DeviceConfig.GetBool(config, "useEntertainmentAPI", false),
            DeviceConfig.GetBool(config, "useEntertainmentAPIV2", false),
            DeviceConfig.GetBool(config, "convertToLinearRgb", false),
            DeviceConfig.GetBool(config, "switchOffOnBlack", true),
            (float)DeviceConfig.GetDouble(config, "brightnessFactor", 1.0),
            DeviceConfig.GetInt(config, "transitiontime", 1),
            DeviceConfig.GetBool(config, "restoreOriginalState", true),
            DeviceConfig.GetString(config, "entertainmentConfigurationId"),
            DeviceConfig.GetInt(config, "groupId", 0),
            Math.Clamp((float)DeviceConfig.GetDouble(config, "blackLevel", 0.0), 0.0f, 1.0f),
            DeviceConfig.GetInt(config, "onBlackTimeToPowerOff", 100),
            DeviceConfig.GetInt(config, "onBlackTimeToPowerOn", 100),
            DeviceConfig.GetInt(config, "handshakeTimeoutMin", 300),
            DeviceConfig.GetInt(config, "handshakeTimeoutMax", 1000),
            DeviceConfig.GetInt(config, "maxRetry", 60),
            ids,
            ColorOrders.Parse(DeviceConfig.GetString(config, "colorOrder", "rgb")));
    }
}

/// <summary>
/// Philips Hue lights through the bridge (port of <c>DriverNetPhilipsHue</c>, sources/led-drivers/net/DriverNetPhilipsHue.cpp):
/// the classic REST API (one light state request per changed light, at most every 100 ms per light) or Hue
/// Entertainment streaming over DTLS (API v1 entertainment groups, or API v2 entertainment configurations).
/// </summary>
public sealed partial class PhilipsHueDriver : IFloatColorDriver
{
    public const string TypeName = "philipshue";

    private static readonly TimeSpan s_streamInterval = TimeSpan.FromMilliseconds(20);

    private readonly HueOptions _options;
    private readonly DriverContext _context;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private HueBridge? _bridge;
    private List<HueLight> _lights = [];
    private int[] _ids = [];
    private Vector3[] _streamColors = [];
    private byte[] _packet = [];
    private DtlsPskChannel? _dtls;
    private bool _entertainment;
    private bool _streaming;
    private string _applicationId = string.Empty;
    private byte _sequence;
    private long _lastConfirm;
    private int _lastId = -1;

    public PhilipsHueDriver(HueOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(options.Address))
        {
            throw new ArgumentException("No target hostname nor IP defined", nameof(options));
        }

        _options = options;
        _context = context;
        _time = context.Time;
        _logger = context.LoggerFactory.CreateLogger<PhilipsHueDriver>();
        _entertainment = WantsEntertainment(options);
        RetryPolicy = new RetryPolicy(Math.Max(0, options.MaxRetry), TimeSpan.FromSeconds(2));
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new PhilipsHueDriver(HueOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
        GetProperties = GetPropertiesAsync,
        Identify = IdentifyAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy { get; }

    public string? Status { get; private set; }

    public bool WritesFloatColors => _entertainment;

    /// <summary>Bridges announced with mDNS (<c>_hue._tcp</c>), else an SSDP search for "IpBridge" servers.</summary>
    public static async Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = new JsonArray();
        foreach (var s in await request.Context.Discovery.Mdns.BrowseAsync(KnownServices.PhilipsHue, TimeSpan.FromSeconds(2), request.CancellationToken))
        {
            var port = s.Port == 443 ? 80 : s.Port;
            var ip = s.Address.ToString();
            list.Add((JsonNode)new JsonObject
            {
                ["ip"] = ip,
                ["port"] = port,
                ["value"] = port == 80 ? ip : $"{ip}:{port}",
                ["name"] = $"Hue bridge {ip} ({s.HostName})",
            });
        }

        return list.Count > 0
            ? list
            : await SsdpDevices.SearchAsync(request, "upnp:rootdevice", 1900, "SERVER", "(.*)IpBridge(.*)", false, (ip, _) => $"Hue bridge {ip}");
    }

    /// <summary>
    /// <c>{host, user, filter}</c>: the bridge resource <c>/api/&lt;user&gt;/&lt;filter&gt;</c>. With <c>pair: true</c>
    /// (and the bridge's link button pressed) it creates a username and client key instead.
    /// </summary>
    public static async Task<JsonObject> GetPropertiesAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = request.Parameters;
        var host = DeviceConfig.GetString(p, "host");
        if (host.Length == 0)
        {
            return [];
        }

        if (DeviceConfig.GetBool(p, "pair", false))
        {
            return await HueBridge.PairAsync(host, request.Context, request.CancellationToken);
        }

        var bridge = new HueBridge(host, DeviceConfig.GetString(p, "user"), false, request.Context);
        var reply = await bridge.GetRawAsync(DeviceConfig.GetString(p, "filter"), request.CancellationToken);
        return reply.Body as JsonObject ?? [];
    }

    /// <summary><c>{host, user, lightId}</c>: the light's "select" alert (one blink).</summary>
    public static async Task IdentifyAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = request.Parameters;
        var host = DeviceConfig.GetString(p, "host");
        if (host.Length == 0)
        {
            return;
        }

        var bridge = new HueBridge(host, DeviceConfig.GetString(p, "user"), false, request.Context);
        await bridge.PutAsync(string.Create(CultureInfo.InvariantCulture, $"lights/{DeviceConfig.GetInt(p, "lightId", 0)}/state"), """{"on":true,"alert":"select"}""", true, request.CancellationToken);
    }

    /// <summary>Entertainment streams at 50 Hz; REST lights are refreshed every 100 ms per light (the bridge's 10 Hz limit).</summary>
    public TimeSpan? EffectiveRefreshInterval(TimeSpan? configured) =>
        _entertainment ? s_streamInterval : TimeSpan.FromMilliseconds(100 * Math.Max(1, _lights.Count));

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        CloseStream();
        _entertainment = WantsEntertainment(_options);
        _bridge = new HueBridge(_options.Address, _options.Username, _options.ApiV2, _context);
        var all = await _bridge.GetAsync(_options.ApiV2 ? "/clip/v2/resource" : string.Empty, cancellationToken) as JsonObject ??
            throw new IOException("Could not read the bridge details");
        if (_options.ApiV2)
        {
            _applicationId = (await _bridge.GetRawAsync("/auth/v1", cancellationToken)).Headers.TryGetValues("hue-application-id", out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
            await SetLightsV2Async(all, cancellationToken);
        }
        else
        {
            CheckApiVersion(all["config"] as JsonObject);
            SetLightsV1(all);
        }

        if (_lights.Count == 0)
        {
            throw new IOException("No light-IDs configured");
        }

        if (ledCount < _lights.Count)
        {
            throw new InvalidOperationException("More light-IDs configured than LEDs, each light-ID requires one LED!");
        }

        _ids = [.. _lights.Select(l => l.Id)];
        _streamColors = new Vector3[_lights.Count];
        if (_entertainment)
        {
            await OpenStreamAsync(cancellationToken);
        }
        else
        {
            await StoreStateAsync(cancellationToken);
            foreach (var light in _lights)
            {
                await SetOnOffAsync(light, true, true, cancellationToken);
            }
        }

        Status = $"{_lights.Count} lights{(_entertainment ? ", streaming" : string.Empty)}";
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        if (_bridge is null || _lights.Count == 0)
        {
            return false;
        }

        var floats = new Vector3[_lights.Count];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = i * 3 + 2 < colors.Length ? new Vector3(colors[i * 3] / 255.0f, colors[(i * 3) + 1] / 255.0f, colors[(i * 3) + 2] / 255.0f) : Vector3.Zero;
        }

        WriteLightsAsync(floats).GetAwaiter().GetResult();
        return true;
    }

    public bool WriteFloat(ReadOnlySpan<Vector3> colors, TimeSpan frameInterval)
    {
        if (_bridge is null || _lights.Count == 0)
        {
            return false;
        }

        var floats = new Vector3[_lights.Count];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = i < colors.Length ? FloatColorOrder.Apply(colors[i], _options.ColorOrder) : Vector3.Zero;
        }

        WriteLightsAsync(floats).GetAwaiter().GetResult();
        return !_entertainment || SendStream();
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_bridge is null)
        {
            return;
        }

        if (_entertainment && _streaming)
        {
            await StopStreamAsync(cancellationToken);
        }
        else if (_options.RestoreOriginalState)
        {
            await RestoreStateAsync(cancellationToken);
        }
        else
        {
            foreach (var light in _lights)
            {
                await SetOnOffAsync(light, false, true, cancellationToken);
            }
        }

        Status = null;
    }

    public void Dispose() => CloseStream();

    private static bool WantsEntertainment(HueOptions o) =>
        o.ApiV2 ? o.EntertainmentConfigurationId.Length > 0 : o.Entertainment && o.GroupId > 0;

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void CheckApiVersion(JsonObject? config)
    {
        var version = config is null ? string.Empty : DeviceConfig.GetString(config, "apiversion");
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var ready = parts.Length >= 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var major) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var minor) &&
            (major > 1 || (major == 1 && minor >= 22));
        LogBridge(config is null ? string.Empty : DeviceConfig.GetString(config, "name"), version, ready);
        if (_entertainment && !ready)
        {
            LogNoEntertainment("the bridge is not Entertainment API ready");
            _entertainment = false;
        }
    }

    private void SetLightsV1(JsonObject all)
    {
        var lightsMap = all["lights"] as JsonObject ?? [];
        IEnumerable<int> ids = _options.LightIds ?? [];
        if (_entertainment)
        {
            var group = all["groups"]?[_options.GroupId.ToString(CultureInfo.InvariantCulture)] as JsonObject;
            var groupLights = group is not null && DeviceConfig.GetString(group, "type") == "Entertainment" ? group["lights"] as JsonArray : null;
            if (groupLights is { Count: > 0 })
            {
                ids = groupLights.Select(n => int.TryParse(n?.ToString(), CultureInfo.InvariantCulture, out var id) ? id : 0);
                var groupName = group?["name"]?.ToString() ?? string.Empty;
                var groupId = _options.GroupId.ToString(CultureInfo.InvariantCulture);
                LogGroup(groupId, groupName, groupLights.Count);
            }
            else
            {
                LogNoEntertainment($"group {_options.GroupId} is not usable");
                _entertainment = false;
            }
        }

        _lights = [];
        var index = 0;
        foreach (var id in ids.Where(id => id > 0).Distinct().Order())
        {
            if (lightsMap[id.ToString(CultureInfo.InvariantCulture)] is JsonObject)
            {
                _lights.Add(new HueLight(id, index, []));
            }
            else
            {
                LogMissingLight(id);
            }

            index++;
        }
    }

    private async Task SetLightsV2Async(JsonObject all, CancellationToken cancellationToken)
    {
        var configuration = (all["data"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(d => DeviceConfig.GetString(d, "type") == "entertainment_configuration" && DeviceConfig.GetString(d, "id") == _options.EntertainmentConfigurationId);
        var channels = configuration?["channels"] as JsonArray;
        if (channels is not { Count: > 0 })
        {
            LogNoEntertainment($"entertainment configuration {_options.EntertainmentConfigurationId} is not usable");
            _entertainment = false;
            _lights = [];
            return;
        }

        var configurationName = configuration?["name"]?.ToString() ?? string.Empty;
        LogGroup(_options.EntertainmentConfigurationId, configurationName, channels.Count);
        _lights = [];
        var index = 0;
        foreach (var channel in channels.OfType<JsonObject>())
        {
            _lights.Add(new HueLight(DeviceConfig.GetInt(channel, "channel_id", 0), index++, await LightIdsInChannelAsync(channel, cancellationToken)));
        }
    }

    /// <summary>The v2 light ids behind a channel: member → entertainment service → owner device → its light services.</summary>
    private async Task<List<string>> LightIdsInChannelAsync(JsonObject channel, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        foreach (var member in (channel["members"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (member["service"] is not JsonObject service || DeviceConfig.GetString(service, "rtype") != "entertainment")
            {
                continue;
            }

            var entertainment = (await _bridge!.GetAsync($"/clip/v2/resource/entertainment/{DeviceConfig.GetString(service, "rid")}", cancellationToken))?["data"]?[0] as JsonObject;
            if (entertainment?["owner"] is not JsonObject owner || DeviceConfig.GetString(owner, "rtype") != "device")
            {
                continue;
            }

            var device = (await _bridge.GetAsync($"/clip/v2/resource/device/{DeviceConfig.GetString(owner, "rid")}", cancellationToken))?["data"]?[0] as JsonObject;
            ids.AddRange((device?["services"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(s => DeviceConfig.GetString(s, "rtype") == "light")
                .Select(s => DeviceConfig.GetString(s, "rid")));
        }

        return ids;
    }

    private async Task OpenStreamAsync(CancellationToken cancellationToken)
    {
        var (active, owner) = await StreamStateAsync(cancellationToken);
        if (active)
        {
            if (!IsStreamOwner(owner))
            {
                throw new IOException($"The entertainment group is in use and owned by another user ({owner}).");
            }

            if (!await StopStreamAsync(cancellationToken))
            {
                throw new IOException("The entertainment group is in use and couldn't be stopped.");
            }
        }

        if (!await SetStreamActiveAsync(true, cancellationToken))
        {
            throw new IOException("The entertainment stream has NOT started.");
        }

        _sequence = 0;
        await StoreStateAsync(cancellationToken);

        var address = await _context.ResolveHost(_bridge!.Host, cancellationToken);
        var attempts = 5;
        _dtls = DtlsPskChannel.Connect(new DtlsPskSettings(
            new IPEndPoint(address, _options.StreamPort),
            System.Text.Encoding.UTF8.GetBytes(_options.Username),
            Convert.FromHexString(_options.ClientKey),
            TimeSpan.FromMilliseconds(_options.HandshakeTimeoutMaxMs * attempts),
            TimeSpan.FromMilliseconds(_options.HandshakeTimeoutMinMs)));
        _packet = new byte[_options.ApiV2 ? HueProtocol.V2Size(_lights.Count, _options.EntertainmentConfigurationId) : HueProtocol.V1Size(_lights.Count)];
        _streaming = true;
        LogStreaming(_bridge.Host);
    }

    private async Task<bool> StopStreamAsync(CancellationToken cancellationToken)
    {
        if (_dtls is not null && !_options.RestoreOriginalState)
        {
            foreach (var light in _lights)
            {
                light.SetColor(Vector3.Zero);
            }

            for (var i = 0; i < 2; i++)
            {
                SendStream();
                await Task.Delay(TimeSpan.FromMilliseconds(5), _time, cancellationToken);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), _time, cancellationToken);
        }

        CloseStream();
        var stopped = false;
        for (var attempt = 0; attempt < 3 && !stopped; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), _time, cancellationToken);
            }

            stopped = await SetStreamActiveAsync(false, cancellationToken);
        }

        _streaming = false;
        if (_options.RestoreOriginalState)
        {
            await RestoreStateAsync(cancellationToken);
        }

        return stopped;
    }

    private void CloseStream()
    {
        _dtls?.Dispose();
        _dtls = null;
    }

    private bool IsStreamOwner(string owner) => owner.Length > 0 && owner == (_options.ApiV2 ? _applicationId : _options.Username);

    private async Task<(bool Active, string Owner)> StreamStateAsync(CancellationToken cancellationToken)
    {
        if (_options.ApiV2)
        {
            var state = (await _bridge!.GetAsync($"/clip/v2/resource/entertainment_configuration/{_options.EntertainmentConfigurationId}", cancellationToken))?["data"]?[0] as JsonObject;
            var status = state is null ? string.Empty : DeviceConfig.GetString(state, "status");
            if (status.Length == 0)
            {
                throw new IOException("No streaming info in the entertainment configuration");
            }

            return (status == "active", state?["active_streamer"] is JsonObject s ? DeviceConfig.GetString(s, "rid") : string.Empty);
        }

        var group = await _bridge!.GetAsync(string.Create(CultureInfo.InvariantCulture, $"groups/{_options.GroupId}"), cancellationToken);
        if (group?["stream"] is not JsonObject stream)
        {
            throw new IOException("No streaming info in the group");
        }

        return (DeviceConfig.GetBool(stream, "active", false), DeviceConfig.GetString(stream, "owner"));
    }

    private async Task<bool> SetStreamActiveAsync(bool active, CancellationToken cancellationToken)
    {
        if (_options.ApiV2)
        {
            await _bridge!.PutAsync($"/clip/v2/resource/entertainment_configuration/{_options.EntertainmentConfigurationId}", active ? """{"action":"start"}""" : """{"action":"stop"}""", true, cancellationToken);
            var (isActive, owner) = await StreamStateAsync(cancellationToken);
            return (isActive && IsStreamOwner(owner)) == active;
        }

        var reply = await _bridge!.PutAsync(string.Create(CultureInfo.InvariantCulture, $"groups/{_options.GroupId}"), active ? """{"stream":{"active":true}}""" : """{"stream":{"active":false}}""", true, cancellationToken);
        if ((reply as JsonArray)?.FirstOrDefault()?["success"] is not JsonObject success)
        {
            return false;
        }

        // the bridge answers {"/groups/<id>/stream/active": true}, diyHue {"/groups/<id>/stream": {"active": true}}
        var path = string.Create(CultureInfo.InvariantCulture, $"/groups/{_options.GroupId}/stream");
        if (success[path + "/active"] is JsonValue v && v.TryGetValue(out bool state))
        {
            return state == active;
        }

        return success[path] is JsonObject diy && DeviceConfig.GetBool(diy, "active", !active) == active;
    }

    private async Task StoreStateAsync(CancellationToken cancellationToken)
    {
        if (!_options.RestoreOriginalState)
        {
            return;
        }

        foreach (var light in _lights)
        {
            if (_options.ApiV2)
            {
                foreach (var id in light.LightIds)
                {
                    var state = (await _bridge!.GetAsync($"/clip/v2/resource/light/{id}", cancellationToken))?["data"]?[0] as JsonObject;
                    if (state is not null)
                    {
                        light.OriginalStatesV2[id] = new JsonObject { ["color"] = state["color"]?.DeepClone(), ["on"] = state["on"]?.DeepClone() }.ToJsonString(LampColor.QtJson);
                    }
                }
            }
            else if (await _bridge!.GetAsync(string.Create(CultureInfo.InvariantCulture, $"lights/{light.Id}"), cancellationToken) is JsonObject values)
            {
                light.SaveOriginalState(values);
            }
        }
    }

    private async Task RestoreStateAsync(CancellationToken cancellationToken)
    {
        if (!_options.RestoreOriginalState)
        {
            return;
        }

        foreach (var light in _lights)
        {
            if (_options.ApiV2)
            {
                foreach (var (id, state) in light.OriginalStatesV2)
                {
                    await _bridge!.PutAsync($"/clip/v2/resource/light/{id}", state, false, cancellationToken);
                }
            }
            else if (light.OriginalState.Length > 0)
            {
                await _bridge!.PutAsync(string.Create(CultureInfo.InvariantCulture, $"lights/{light.Id}/state"), light.OriginalState, false, cancellationToken);
            }
        }
    }

    private bool SendStream()
    {
        if (_dtls is null)
        {
            return false;
        }

        for (var i = 0; i < _lights.Count; i++)
        {
            _streamColors[i] = _lights[i].Color;
        }

        var length = _options.ApiV2
            ? HueProtocol.WriteV2(_packet, _sequence++, _options.EntertainmentConfigurationId, _ids, _streamColors)
            : HueProtocol.WriteV1(_packet, _sequence++, _ids, _streamColors);
        return _dtls.Send(_packet, 0, length);
    }

    /// <summary>Port of <c>writeSingleLightsGeneric</c>: per light the xy color, on/off and the switch-off-on-black timers.</summary>
    private async Task WriteLightsAsync(Vector3[] colors)
    {
        var ct = CancellationToken.None;
        for (var i = 0; i < _lights.Count; i++)
        {
            var light = _lights[i];
            var xy = HueProtocol.RgbToXyb(colors[i], _options.ConvertToLinearRgb);
            if (_options.ApiV2)
            {
                await SetOnOffAsync(light, true, false, ct);
                SetStreamColor(light, xy);
                continue;
            }

            var now = NowMs();
            if (_options.SwitchOffOnBlack && xy.Z <= _options.BlackLevel && light.IsBlack(true, now, _options.OnBlackTimeToPowerOffMs))
            {
                xy = Vector3.Zero;
                if (_entertainment)
                {
                    if (light.On)
                    {
                        SetStreamColor(light, xy);
                        await SetOnOffAsync(light, false, false, ct);
                    }
                }
                else if (light.On)
                {
                    await SetStateAsync(light, false, xy, ct);
                }
            }
            else
            {
                var wasOn = light.On;
                if (_options.SwitchOffOnBlack && xy.Z > _options.BlackLevel && light.IsWhite(true, now, _options.OnBlackTimeToPowerOnMs))
                {
                    if (!wasOn)
                    {
                        xy.Z *= 0.5f;
                    }

                    await TurnOnWithColorAsync(light, xy, ct);
                }
                else if (!_options.SwitchOffOnBlack)
                {
                    await TurnOnWithColorAsync(light, xy, ct);
                }
            }

            if (xy.Z > _options.BlackLevel)
            {
                light.IsBlack(false, now, 0);
            }

            if (xy.Z <= _options.BlackLevel)
            {
                light.IsWhite(false, now, 0);
            }
        }
    }

    private async Task TurnOnWithColorAsync(HueLight light, Vector3 xy, CancellationToken ct)
    {
        if (_entertainment)
        {
            await SetOnOffAsync(light, true, false, ct);
            SetStreamColor(light, xy);
        }
        else
        {
            await SetStateAsync(light, true, xy, ct);
        }
    }

    /// <summary>Entertainment color: brightness scaled by the factor (capped at 1).</summary>
    private void SetStreamColor(HueLight light, Vector3 xy)
    {
        if (!light.HasColor || light.Color != xy)
        {
            xy.Z = MathF.Min(1.0f, _options.BrightnessFactor * MathF.Max(0.0f, xy.Z));
            light.SetColor(xy);
        }
    }

    private async Task SetOnOffAsync(HueLight light, bool on, bool force, CancellationToken ct)
    {
        if (light.On == on && !force)
        {
            return;
        }

        light.On = on;
        var state = on ? "true" : "false";
        if (_options.ApiV2)
        {
            foreach (var id in light.LightIds)
            {
                await _bridge!.PutAsync($"/clip/v2/resource/light/{id}", "{\"on\":{\"on\": " + state + " }}", false, ct);
            }

            return;
        }

        await _bridge!.PutAsync(string.Create(CultureInfo.InvariantCulture, $"lights/{light.Id}/state"), "{\"on\": " + state + " }", false, ct);
    }

    /// <summary>Port of <c>setState</c>: one REST request with on/transition/xy/brightness, power-off sent separately.</summary>
    private async Task SetStateAsync(HueLight light, bool on, Vector3 xy, CancellationToken ct)
    {
        var stateCmd = string.Empty;
        var powerCmd = string.Empty;
        var priority = false;
        if (light.On != on)
        {
            light.On = on;
            powerCmd = $"\"on\":{(on ? "true" : "false")}";
            priority = true;
        }

        if (light.TransitionTime != _options.TransitionTime)
        {
            light.TransitionTime = _options.TransitionTime;
            stateCmd += string.Create(CultureInfo.InvariantCulture, $"\"transitiontime\":{_options.TransitionTime},");
        }

        var bri = HueProtocol.RestBrightness(xy.Z, _options.BrightnessFactor);
        var now = NowMs();
        if ((!light.HasColor || light.Color != xy) && (!light.IsBusy(now) || priority))
        {
            light.SetColor(xy);
            stateCmd += HueProtocol.XyBri(xy, bri) + ",";
        }

        if (stateCmd.Length == 0 && powerCmd.Length == 0)
        {
            return;
        }

        if ((now - _lastConfirm > 1500 && light.Id != _lastId) || now - _lastConfirm > 3000)
        {
            _lastId = light.Id;
            _lastConfirm = now;
        }

        var route = string.Create(CultureInfo.InvariantCulture, $"lights/{light.Id}/state");
        if (stateCmd.Length > 0)
        {
            await _bridge!.PutAsync(route, "{\"on\":true," + stateCmd[..^1] + "}", false, ct);
        }

        if (powerCmd.Length > 0 && !on)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), _time, ct);
            await _bridge!.PutAsync(route, "{" + powerCmd + "}", false, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Hue bridge {Name}: API {Version}, Entertainment API ready: {Ready}")]
    private partial void LogBridge(string name, string version, bool ready);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hue entertainment group {Id} \"{Name}\" with {Count} lights / channels")]
    private partial void LogGroup(string id, string name, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hue: Entertainment API usage disabled: {Reason}")]
    private partial void LogNoEntertainment(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hue: configured light {Id} is not available at this bridge")]
    private partial void LogMissingLight(int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hue bridge {Host}: Entertainment API connected, streaming")]
    private partial void LogStreaming(string host);
}

/// <summary>State of one Hue light or entertainment channel (port of <c>PhilipsHueLight</c>).</summary>
internal sealed class HueLight(int id, int ledIndex, List<string> lightIds)
{
    private long _lastSend;
    private long _lastBlack;
    private long _lastWhite;

    public int Id { get; } = id;

    public int LedIndex { get; } = ledIndex;

    public IReadOnlyList<string> LightIds { get; } = lightIds;

    public bool On { get; set; }

    public int TransitionTime { get; set; }

    public Vector3 Color { get; private set; }

    public bool HasColor { get; private set; }

    public string OriginalState { get; private set; } = string.Empty;

    public Dictionary<string, string> OriginalStatesV2 { get; } = new(StringComparer.Ordinal);

    public void SetColor(Vector3 color)
    {
        HasColor = true;
        Color = color;
    }

    /// <summary>True when the last color was sent less than 100 ms ago (otherwise remembers now).</summary>
    public bool IsBusy(long now)
    {
        if (now - _lastSend >= 100)
        {
            _lastSend = now;
            return false;
        }

        return true;
    }

    /// <summary>True once the light has been black for <paramref name="timeMs"/>.</summary>
    public bool IsBlack(bool black, long now, int timeMs)
    {
        if (!black)
        {
            _lastBlack = 0;
            return false;
        }

        if (_lastBlack == 0)
        {
            _lastBlack = now;
            return false;
        }

        return now - _lastBlack >= timeMs;
    }

    public bool IsWhite(bool white, long now, int timeMs)
    {
        if (!white)
        {
            _lastWhite = 0;
            return false;
        }

        if (_lastWhite == 0)
        {
            _lastWhite = now;
            return false;
        }

        return now - _lastWhite >= timeMs;
    }

    /// <summary>Keeps the light's on/xy/brightness to restore it later (<c>{"bri":..,"on":true,"xy":[..]}</c> or <c>{"on":false}</c>).</summary>
    public void SaveOriginalState(JsonObject values)
    {
        if (values["state"] is not JsonObject state)
        {
            return;
        }

        var saved = new JsonObject();
        var on = DeviceConfig.GetBool(state, "on", false);
        if (on)
        {
            saved["bri"] = state["bri"]?.DeepClone();
            saved["on"] = state["on"]?.DeepClone();
            saved["xy"] = state["xy"]?.DeepClone();
            On = true;
            TransitionTime = DeviceConfig.GetInt(state, "transitiontime", 0);
        }
        else
        {
            saved["on"] = state["on"]?.DeepClone();
        }

        OriginalState = saved.ToJsonString(LampColor.QtJson);
    }
}
