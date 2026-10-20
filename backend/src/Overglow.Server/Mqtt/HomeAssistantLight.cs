using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Core.Color;
using Overglow.Core.Priorities;
using Overglow.Engine;
using Overglow.Mqtt;

namespace Overglow.Server.Mqtt;

/// <summary>
/// The lights as one Home Assistant light entity through MQTT discovery (not in the C++ build): a retained
/// <c>homeassistant/light/&lt;node&gt;/light/config</c> with the JSON schema, on/off (<c>ALL</c>), RGB color with
/// brightness (a static color, scaled by the brightness) and as effects "Screen sync" (screen capture again) and every
/// effect including music mode (run at priority 1 until something else is chosen), plus a
/// retained state on <c>&lt;topic&gt;/light/state</c> that follows every change from any client. Availability is the
/// integration's <c>&lt;topic&gt;/status</c>.
/// </summary>
public sealed partial class HomeAssistantLight : IAsyncDisposable
{
    public const string DiscoveryPrefix = "homeassistant";
    public const string ScreenEffect = "Screen sync";

    /// <summary>Priority of colors set from Home Assistant (the tray's slot: one "user color" at a time).</summary>
    public const int ColorPriority = 1;

    private readonly MqttConnection _mqtt;
    private readonly LightingEngine _engine;
    private readonly MqttIntegrationSettings _topics;
    private readonly ServerOptions _server;
    private readonly Func<string> _deviceName;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _publish = new(1, 1);
    private readonly Lock _lock = new();
    private IAsyncDisposable? _subscription;
    private Rgb8 _color = Rgb8.White;
    private int _brightness = 255;
    private string? _lastState;
    private bool _disposed;

    private HomeAssistantLight(MqttConnection mqtt, LightingEngine engine, MqttIntegrationSettings topics, ServerOptions server, Func<string> deviceName, ILogger logger)
    {
        _mqtt = mqtt;
        _engine = engine;
        _topics = topics;
        _server = server;
        _deviceName = deviceName;
        _logger = logger;
    }

    public string CommandTopic => $"{_topics.Topic}/light/set";

    public string StateTopic => $"{_topics.Topic}/light/state";

    public string ConfigTopic => ConfigTopicFor(_server.InstanceId);

    public static string ConfigTopicFor(string instanceId) => $"{DiscoveryPrefix}/light/{NodeId(instanceId)}/light/config";

    /// <param name="deviceName">The device name Home Assistant shows (<c>general.name</c>).</param>
    public static async Task<HomeAssistantLight> StartAsync(
        MqttConnection mqtt, LightingEngine engine, MqttIntegrationSettings topics, ServerOptions server, Func<string> deviceName, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mqtt);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(deviceName);
        ArgumentNullException.ThrowIfNull(logger);
        var light = new HomeAssistantLight(mqtt, engine, topics, server, deviceName, logger);
        light._subscription = await mqtt.SubscribeAsync(light.CommandTopic, (_, payload) => light.OnCommand(payload), cancellationToken);
        engine.Components.Changed += light.OnComponentChanged;
        engine.PrioritiesChanged += light.OnPrioritiesChanged;
        mqtt.Connected += light.OnConnected;
        if (mqtt.IsConnected)
        {
            await light.AnnounceAsync(cancellationToken);
        }

        light.LogStarted(light.ConfigTopic);
        return light;
    }

    /// <summary>Removes the entity from Home Assistant (empty retained config), e.g. when discovery is switched off.</summary>
    public static Task<bool> RemoveAsync(MqttConnection mqtt, string instanceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mqtt);
        return mqtt.PublishAsync(new MqttMessage(ConfigTopicFor(instanceId), string.Empty, Qos: 1, Retain: true), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _mqtt.Connected -= OnConnected;
        _engine.Components.Changed -= OnComponentChanged;
        _engine.PrioritiesChanged -= OnPrioritiesChanged;
        if (_subscription is { } subscription)
        {
            await subscription.DisposeAsync();
        }

        await _publish.WaitAsync();
        _publish.Release();
        _publish.Dispose();
    }

    /// <summary>The discovery config (exposed for tests).</summary>
    internal JsonObject Config() => new()
    {
        ["name"] = null,
        ["unique_id"] = $"{NodeId(_server.InstanceId)}_light",
        ["schema"] = "json",
        ["command_topic"] = CommandTopic,
        ["state_topic"] = StateTopic,
        ["availability_topic"] = _topics.AvailabilityTopic,
        ["payload_available"] = MqttIntegration.Online,
        ["payload_not_available"] = MqttIntegration.Offline,
        ["brightness"] = true,
        ["brightness_scale"] = 255,
        ["supported_color_modes"] = new JsonArray("rgb"),
        ["effect"] = true,
        ["effect_list"] = new JsonArray([ScreenEffect, .. EffectEngine.Available.Select(e => (JsonNode)e.Name)]),
        ["device"] = new JsonObject
        {
            ["identifiers"] = new JsonArray(NodeId(_server.InstanceId)),
            ["name"] = _deviceName(),
            ["manufacturer"] = "Overglow",
            ["model"] = "Overglow ambient lighting",
            ["sw_version"] = _server.Version,
        },
        ["origin"] = new JsonObject { ["name"] = "Overglow", ["sw_version"] = _server.Version },
    };

    /// <summary>The state as Home Assistant reads it (exposed for tests).</summary>
    internal JsonObject State()
    {
        var visible = _engine.Sources.Visible;
        Rgb8 color;
        int brightness;
        lock (_lock)
        {
            color = visible.HasSource && visible.Component == SourceComponent.Color && visible.Priority != ColorPriority ? visible.Color : _color;
            brightness = _brightness;
        }

        var state = new JsonObject
        {
            ["state"] = _engine.Components.IsOn(Component.All) ? "ON" : "OFF",
            ["color_mode"] = "rgb",
            ["brightness"] = brightness,
            ["color"] = new JsonObject { ["r"] = color.R, ["g"] = color.G, ["b"] = color.B },
        };
        state["effect"] = EffectName(visible);
        return state;
    }

    /// <summary>
    /// The running effect's name; "Screen sync" while the screen shows or will with its next frame (it is a source only
    /// while it delivers); null for a color.
    /// </summary>
    private string? EffectName(VisibleSource visible) => visible.Component switch
    {
        SourceComponent.Effect => (visible.Renderer as EffectSource)?.Definition.Name,
        SourceComponent.SystemGrabber => ScreenEffect,
        _ when !visible.HasSource && _engine.Components.IsOn(Component.SystemGrabber) => ScreenEffect,
        _ => null,
    };

    /// <summary>Applies a command of the JSON schema (exposed for tests).</summary>
    internal void Apply(JsonObject command)
    {
        var state = command["state"] is JsonValue s && s.TryGetValue(out string? text) ? text : null;
        if (string.Equals(state, "OFF", StringComparison.OrdinalIgnoreCase))
        {
            _engine.Components.Set(Component.All, false);
            return;
        }

        var effect = command["effect"] is JsonValue e && e.TryGetValue(out string? name) ? name : null;
        if (effect == ScreenEffect)
        {
            _engine.Clear(-1);
            _engine.Components.Set(Component.SystemGrabber, true);
        }
        else if (effect is null || !_engine.RunEffect(effect, ColorPriority, 0, "Home Assistant"))
        {
            var hasColor = command["color"] is JsonObject;
            var hasBrightness = command["brightness"] is JsonValue;
            Rgb8? show = null;
            lock (_lock)
            {
                if (command["color"] is JsonObject rgb)
                {
                    _color = new Rgb8(Channel(rgb, "r"), Channel(rgb, "g"), Channel(rgb, "b"));
                }

                if (command["brightness"] is JsonValue level && level.TryGetValue(out double value))
                {
                    _brightness = Math.Clamp((int)Math.Round(value), 0, 255);
                }

                var visible = _engine.Sources.Visible;
                if (hasColor || (hasBrightness && visible.Component == SourceComponent.Color && visible.Priority == ColorPriority))
                {
                    show = Scale(_color, _brightness);
                }
            }

            if (show is { } color)
            {
                _engine.SetColor(ColorPriority, color, 0, "Home Assistant");
            }
        }

        _engine.Components.Set(Component.All, true);
        PublishState();

        static byte Channel(JsonObject o, string name) =>
            o[name] is JsonValue v && v.TryGetValue(out double d) ? (byte)Math.Clamp((int)Math.Round(d), 0, 255) : (byte)0;
    }

    internal static Rgb8 Scale(Rgb8 color, int brightness) => new(
        (byte)Math.Round(color.R * brightness / 255.0),
        (byte)Math.Round(color.G * brightness / 255.0),
        (byte)Math.Round(color.B * brightness / 255.0));

    private static string NodeId(string instanceId) =>
        "overglow_" + new string(instanceId.Where(char.IsAsciiLetterOrDigit).Take(12).ToArray()).ToLowerInvariant();

    private void OnCommand(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is JsonObject command)
            {
                Apply(command);
            }
        }
        catch (JsonException ex)
        {
            LogBadCommand(ex.Message);
        }
    }

    private void OnConnected() => _ = AnnounceAsync(CancellationToken.None);

    private void OnComponentChanged(Component component, bool on) => PublishState();

    private void OnPrioritiesChanged() => PublishState();

    private async Task AnnounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _mqtt.PublishAsync(new MqttMessage(ConfigTopic, Config().ToJsonString(), Qos: 1, Retain: true), cancellationToken);
            lock (_lock)
            {
                _lastState = null;
            }

            await PublishStateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogPublishFailed(ex.Message);
        }
    }

    private void PublishState() => _ = PublishStateAsync(CancellationToken.None);

    // Only changes are sent; the semaphore keeps them in order.
    private async Task PublishStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _publish.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var state = State().ToJsonString();
            lock (_lock)
            {
                if (_disposed || state == _lastState)
                {
                    return;
                }

                _lastState = state;
            }

            await _mqtt.PublishAsync(new MqttMessage(StateTopic, state, Qos: 1, Retain: true), cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
        {
            LogPublishFailed(ex.Message);
        }
        finally
        {
            _publish.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT: Home Assistant discovery on {Topic}")]
    private partial void LogStarted(string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: ignored a Home Assistant command that isn't JSON: {Reason}")]
    private partial void LogBadCommand(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: couldn't publish to Home Assistant: {Reason}")]
    private partial void LogPublishFailed(string reason);
}
