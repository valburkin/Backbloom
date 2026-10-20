using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace Overglow.Mqtt;

/// <summary>One message to publish.</summary>
/// <param name="Qos">MQTT quality of service: 0 (at most once), 1 (at least once) or 2 (exactly once).</param>
/// <param name="Retain">The broker keeps it for later subscribers.</param>
public sealed record MqttMessage(string Topic, string Payload, int Qos = 0, bool Retain = false);

/// <summary>Settings of the <c>mqtt</c> section that concern the broker connection.</summary>
public sealed record MqttSettings(bool Enabled, string Host, int Port = 1883, string Username = "", string Password = "", bool UseTls = false, bool IgnoreTlsErrors = true, int MaxRetry = 0)
{
    public static MqttSettings Disabled { get; } = new(false, string.Empty);

    /// <summary>
    /// Last will: published by the broker when the connection breaks, and by <see cref="MqttConnection"/> itself
    /// right before it disconnects, so an availability topic reads "offline" either way.
    /// </summary>
    public MqttMessage? Will { get; init; }

    public static MqttSettings FromJson(JsonObject? section)
    {
        if (section is null)
        {
            return Disabled;
        }

        return new MqttSettings(
            Bool(section, "enable", false),
            Text(section, "host"),
            Int(section, "port", 1883),
            Text(section, "username"),
            Text(section, "password"),
            Bool(section, "is_ssl", false),
            Bool(section, "ignore_ssl_errors", true),
            Int(section, "maxRetry", 0));

        static string Text(JsonObject o, string n) => o[n] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;
        static int Int(JsonObject o, string n, int f) => o[n] is JsonValue v && v.TryGetValue(out double d) ? (int)d : f;
        static bool Bool(JsonObject o, string n, bool f) => o[n] is JsonValue v && v.TryGetValue(out bool b) ? b : f;
    }

    /// <summary>Settings without the password, for logs.</summary>
    public override string ToString() => $"{Host}:{Port} (TLS: {UseTls}, user: {(Username.Length > 0 ? Username : "none")})";
}

/// <summary>
/// The application's connection to an MQTT broker. Several users (the Zigbee2MQTT driver now, the MQTT integration
/// later) publish and subscribe through it, so there is one client per process.
/// </summary>
public interface IMqttConnection
{
    bool IsConnected { get; }

    /// <summary>Publishes a UTF-8 text message; false when not connected or the broker refused it.</summary>
    Task<bool> PublishAsync(string topic, string payload, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to <paramref name="topic"/> until the returned handle is disposed. <paramref name="received"/> gets
    /// (topic, payload) on a client thread.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(string topic, Action<string, string> received, CancellationToken cancellationToken);

    /// <summary>
    /// Messages <paramref name="owner"/> wants published when the connection is closed (e.g. "switch the lamps off");
    /// null or empty removes them. Port of <c>SignalMqttLastWill</c>.
    /// </summary>
    void SetShutdownMessages(string owner, IReadOnlyList<KeyValuePair<string, string>>? messages);
}

/// <summary><see cref="IMqttConnection"/> over MQTTnet (MIT), configured from the <c>mqtt</c> settings.</summary>
public sealed partial class MqttConnection : IMqttConnection, IAsyncDisposable
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly IMqttClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Action<string, string>>> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>> _shutdownMessages = new(StringComparer.Ordinal);
    private MqttSettings _settings = MqttSettings.Disabled;
    private CancellationTokenSource? _reconnect;
    private bool _disposed;

    public MqttConnection(ILogger<MqttConnection> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            Dispatch(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty);
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += e =>
        {
            if (e.ClientWasConnected)
            {
                LogDisconnected(e.Reason);
                StartReconnecting();
            }

            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Raised on a client thread after every successful (re)connect, once the subscriptions are restored.</summary>
    public event Action? Connected;

    /// <summary>Connects with, or disconnects for, new settings (an unchanged configuration keeps the connection).</summary>
    public async Task ConfigureAsync(MqttSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (settings == _settings && (_client.IsConnected || !settings.Enabled))
            {
                return;
            }

            _reconnect?.Cancel();
            await DisconnectCoreAsync(cancellationToken);
            _settings = settings;
            if (settings.Enabled && settings.Host.Length > 0)
            {
                await ConnectCoreAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> PublishAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        if (!_client.IsConnected)
        {
            return false;
        }

        return await PublishAsync(new MqttMessage(topic, payload ?? string.Empty), cancellationToken);
    }

    /// <summary>Publishes with the message's QoS and retain flag; false when not connected or the broker refused it.</summary>
    public async Task<bool> PublishAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(message.Topic);
        if (!_client.IsConnected)
        {
            return false;
        }

        var built = new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(Encoding.UTF8.GetBytes(message.Payload))
            .WithQualityOfServiceLevel(ToQos(message.Qos))
            .WithRetainFlag(message.Retain)
            .Build();
        var result = await _client.PublishAsync(built, cancellationToken);
        return result.IsSuccess;
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string topic, Action<string, string> received, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(received);
        bool first;
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(topic, out var handlers))
            {
                handlers = [];
                _subscriptions[topic] = handlers;
            }

            first = handlers.Count == 0;
            handlers.Add(received);
        }

        if (first && _client.IsConnected)
        {
            await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(topic).Build(), cancellationToken);
        }

        return new Subscription(this, topic, received);
    }

    public void SetShutdownMessages(string owner, IReadOnlyList<KeyValuePair<string, string>>? messages)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        lock (_lock)
        {
            if (messages is { Count: > 0 })
            {
                _shutdownMessages[owner] = messages;
            }
            else
            {
                _shutdownMessages.Remove(owner);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reconnect?.Cancel();
        await _gate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await DisconnectCoreAsync(timeout.Token);
        }
        finally
        {
            _gate.Release();
        }

        _client.Dispose();
        _gate.Dispose();
        _reconnect?.Dispose();
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _settings;
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId($"Overglow-{Environment.MachineName}-{Environment.ProcessId}")
            .WithTimeout(TimeSpan.FromSeconds(5));
        if (settings.Username.Length > 0)
        {
            builder = builder.WithCredentials(settings.Username, settings.Password);
        }

        if (settings.Will is { } will)
        {
            builder = builder
                .WithWillTopic(will.Topic)
                .WithWillPayload(Encoding.UTF8.GetBytes(will.Payload))
                .WithWillQualityOfServiceLevel(ToQos(will.Qos))
                .WithWillRetain(will.Retain);
        }

        if (settings.UseTls)
        {
            builder = builder.WithTlsOptions(o =>
            {
                o.UseTls();
                if (settings.IgnoreTlsErrors)
                {
                    o.WithCertificateValidationHandler(_ => true);
                }
            });
        }

        try
        {
            await _client.ConnectAsync(builder.Build(), cancellationToken);
            LogConnected(settings);
            string[] topics;
            lock (_lock)
            {
                topics = [.. _subscriptions.Where(s => s.Value.Count > 0).Select(s => s.Key)];
            }

            foreach (var topic in topics)
            {
                await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(topic).Build(), cancellationToken);
            }

            Connected?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogConnectFailed(settings, ex.Message);
            StartReconnecting();
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        KeyValuePair<string, string>[] messages;
        lock (_lock)
        {
            messages = [.. _shutdownMessages.Values.SelectMany(m => m)];
            _shutdownMessages.Clear();
        }

        foreach (var (topic, payload) in messages)
        {
            await PublishAsync(topic, payload, cancellationToken);
        }

        if (_settings.Will is { } will)
        {
            await PublishAsync(will, cancellationToken);
        }

        await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
    }

    private static MqttQualityOfServiceLevel ToQos(int qos) => qos switch
    {
        1 => MqttQualityOfServiceLevel.AtLeastOnce,
        2 => MqttQualityOfServiceLevel.ExactlyOnce,
        _ => MqttQualityOfServiceLevel.AtMostOnce,
    };

    private void StartReconnecting()
    {
        var settings = _settings;
        if (!settings.Enabled || settings.MaxRetry <= 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _reconnect, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= settings.MaxRetry && !cts.IsCancellationRequested; attempt++)
            {
                await Task.Delay(s_retryDelay, cts.Token).ConfigureAwait(false);
                await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    if (_client.IsConnected || _settings != settings)
                    {
                        return;
                    }

                    LogRetrying(attempt, settings.MaxRetry);
                    await ConnectCoreAsync(cts.Token).ConfigureAwait(false);
                    if (_client.IsConnected)
                    {
                        return;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }, cts.Token);
    }

    private void Dispatch(string topic, string payload)
    {
        Action<string, string>[] handlers;
        lock (_lock)
        {
            handlers = [.. _subscriptions.Where(s => MqttTopicFilterComparer.Compare(topic, s.Key) == MqttTopicFilterCompareResult.IsMatch).SelectMany(s => s.Value)];
        }

        foreach (var handler in handlers)
        {
            handler(topic, payload);
        }
    }

    private async ValueTask UnsubscribeAsync(string topic, Action<string, string> received)
    {
        bool last;
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(topic, out var handlers) || !handlers.Remove(received))
            {
                return;
            }

            last = handlers.Count == 0;
        }

        if (last && _client.IsConnected)
        {
            await _client.UnsubscribeAsync(new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(topic).Build());
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT: connected to {Broker}")]
    private partial void LogConnected(MqttSettings broker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: couldn't connect to {Broker}: {Reason}")]
    private partial void LogConnectFailed(MqttSettings broker, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: connection lost ({Reason})")]
    private partial void LogDisconnected(MqttClientDisconnectReason reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT: reconnecting ({Attempt}/{MaxRetry})")]
    private partial void LogRetrying(int attempt, int maxRetry);

    private sealed class Subscription(MqttConnection owner, string topic, Action<string, string> received) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => owner.UnsubscribeAsync(topic, received);
    }
}
