using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Overglow.Engine;
using Overglow.Mqtt;
using Overglow.Server.Rpc;
using Overglow.Settings;

namespace Overglow.Server.Mqtt;

/// <summary>
/// The MQTT integration (port of the C++ <c>mqtt</c> module on top of the shared <see cref="MqttConnection"/>):
/// connects with the <c>mqtt</c> settings (again on every change), answers JSON-RPC on
/// <c>&lt;topic&gt;/JsonAPI</c> unless <c>disableApiAccess</c> (<see cref="MqttApi"/>), and, beyond C++, keeps
/// <c>&lt;topic&gt;/status</c> at "online" / "offline" (retained, the connection's last will) and announces a Home
/// Assistant light (<see cref="HomeAssistantLight"/>) when <c>homeAssistantDiscovery</c> is on.
/// </summary>
public sealed partial class MqttIntegration(
    MqttConnection mqtt,
    RpcDispatcher dispatcher,
    ISettingsStore settings,
    LightingEngine engine,
    ServerOptions server,
    ILogger<MqttIntegration> logger) : IHostedService, IAsyncDisposable
{
    public const string Online = "online";
    public const string Offline = "offline";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private MqttIntegrationSettings? _applied;

    // set once the API and the light run: only then is "online" announced (also after a reconnect)
    private volatile MqttIntegrationSettings? _ready;
    private MqttApi? _api;
    private HomeAssistantLight? _light;

    /// <summary>The running Home Assistant bridge (null when off); for tests.</summary>
    internal HomeAssistantLight? Light => _light;

    internal MqttApi? Api => _api;

    /// <summary>The first connection attempt, which runs in the background so an unreachable broker doesn't delay the start.</summary>
    internal Task Started { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += OnSettingsChanged;
        mqtt.Connected += OnConnected;
        Started = ApplyLoggedAsync(settings.GetSection("mqtt") as JsonObject);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnSettingsChanged;
        mqtt.Connected -= OnConnected;

        // a connection attempt in progress ends within its 5 s timeout; stopping goes on regardless of the host's token
        var entered = await _gate.WaitAsync(TimeSpan.FromSeconds(6), CancellationToken.None);
        try
        {
            await StopPartsAsync();
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopPartsAsync();
        _gate.Dispose();
    }

    /// <summary>Applies an <c>mqtt</c> section: reconnects if the connection settings changed, restarts the parts.</summary>
    internal async Task ApplyAsync(JsonObject? section, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var integration = MqttIntegrationSettings.FromJson(section);
            var connection = MqttSettings.FromJson(section) with { Will = new MqttMessage(integration.AvailabilityTopic, Offline, Qos: 1, Retain: true) };
            _ready = null;
            if (integration != _applied)
            {
                await StopPartsAsync();
            }

            await mqtt.ConfigureAsync(connection, cancellationToken);
            _applied = integration;
            if (!connection.Enabled)
            {
                return;
            }

            if (integration.ApiAccess)
            {
                _api ??= await MqttApi.StartAsync(mqtt, dispatcher, integration, logger, cancellationToken);
            }
            else
            {
                LogApiDisabled();
            }

            if (integration.HomeAssistant)
            {
                _light ??= await HomeAssistantLight.StartAsync(mqtt, engine, integration, server, DeviceName, logger, cancellationToken);
            }
            else
            {
                await HomeAssistantLight.RemoveAsync(mqtt, server.InstanceId, cancellationToken);
            }

            _ready = integration;
            await PublishOnlineAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string DeviceName() =>
        settings.GetSection("general")?["name"] is JsonValue v && v.TryGetValue(out string? name) && name.Length > 0 ? name : "Overglow";

    private async Task StopPartsAsync()
    {
        if (_api is { } api)
        {
            _api = null;
            await api.DisposeAsync();
        }

        if (_light is { } light)
        {
            _light = null;
            await light.DisposeAsync();
        }
    }

    private Task<bool> PublishOnlineAsync(CancellationToken cancellationToken) =>
        _ready is { } current ? mqtt.PublishAsync(new MqttMessage(current.AvailabilityTopic, Online, Qos: 1, Retain: true), cancellationToken) : Task.FromResult(false);

    private void OnConnected() => _ = Task.Run(async () =>
    {
        try
        {
            await PublishOnlineAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(ex.Message);
        }
    });

    private void OnSettingsChanged(SettingsChange change)
    {
        if (change.Sections.TryGetValue("mqtt", out var section))
        {
            _ = ApplyLoggedAsync(section as JsonObject);
        }
    }

    private Task ApplyLoggedAsync(JsonObject? section) => Task.Run(async () =>
    {
        try
        {
            await ApplyAsync(section, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
        {
            LogFailed(ex.Message);
        }
    });

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT: access to the JSON-RPC API is disabled (disableApiAccess)")]
    private partial void LogApiDisabled();

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: couldn't apply the settings: {Reason}")]
    private partial void LogFailed(string reason);
}
