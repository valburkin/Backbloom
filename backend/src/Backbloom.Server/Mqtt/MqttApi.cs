using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Backbloom.Mqtt;
using Backbloom.Server.Rpc;

namespace Backbloom.Server.Mqtt;

/// <summary>The integration's part of the <c>mqtt</c> section (the broker connection is <see cref="MqttSettings"/>).</summary>
/// <param name="Topic"><c>custom_topic</c>, or "Backbloom" when empty (HyperHDR used "HyperHDR").</param>
/// <param name="ApiAccess">Not <c>disableApiAccess</c>: JSON-RPC requests are taken from <see cref="ApiTopic"/>.</param>
/// <param name="HomeAssistant"><c>homeAssistantDiscovery</c> (not in C++): announce a light entity to Home Assistant.</param>
public sealed record MqttIntegrationSettings(string Topic, bool ApiAccess, bool HomeAssistant)
{
    public const string DefaultTopic = "Backbloom";

    /// <summary>Requests: one JSON-RPC object or an array of them (C++ <c>%1/JsonAPI</c>).</summary>
    public string ApiTopic => $"{Topic}/JsonAPI";

    /// <summary>The replies of one request message as a JSON array (C++ <c>%1/JsonAPI/response</c>).</summary>
    public string ResponseTopic => $"{Topic}/JsonAPI/response";

    /// <summary>"online" while connected, "offline" otherwise (retained; the connection's last will).</summary>
    public string AvailabilityTopic => $"{Topic}/status";

    public static MqttIntegrationSettings FromJson(JsonObject? section)
    {
        var topic = section?["custom_topic"] is JsonValue t && t.TryGetValue(out string? s) ? s.Trim() : string.Empty;
        return new MqttIntegrationSettings(
            topic.Length > 0 ? topic : DefaultTopic,
            !(section?["disableApiAccess"] is JsonValue d && d.TryGetValue(out bool disabled) && disabled),
            section?["homeAssistantDiscovery"] is not JsonValue h || !h.TryGetValue(out bool ha) || ha);
    }
}

/// <summary>
/// JSON-RPC over MQTT (port of the C++ <c>mqtt::received</c> / <c>executeJson</c>): every message on
/// <c>&lt;topic&gt;/JsonAPI</c> is run with API rights but without configuration access (anyone who can publish on the
/// broker could send it; HyperHDR gave it the rights of a local client), and all replies it produced are published as one JSON array on
/// <c>&lt;topic&gt;/JsonAPI/response</c> (QoS 2, as in C++). A message that isn't JSON gets
/// <c>{ success: false, error }</c>. Messages are handled one at a time, in order.
/// </summary>
public sealed partial class MqttApi : IAsyncDisposable
{
    private readonly MqttConnection _mqtt;
    private readonly RpcDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Channel<string> _requests = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly string _responseTopic;
    private readonly Task _worker;
    private IAsyncDisposable? _subscription;

    private MqttApi(MqttConnection mqtt, RpcDispatcher dispatcher, MqttIntegrationSettings topics, ILogger logger)
    {
        _mqtt = mqtt;
        _dispatcher = dispatcher;
        _logger = logger;
        _responseTopic = topics.ResponseTopic;
        _worker = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Subscribes to the request topic (kept across reconnects by the connection).</summary>
    public static async Task<MqttApi> StartAsync(
        MqttConnection mqtt, RpcDispatcher dispatcher, MqttIntegrationSettings topics, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mqtt);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(logger);
        var api = new MqttApi(mqtt, dispatcher, topics, logger);
        api._subscription = await mqtt.SubscribeAsync(topics.ApiTopic, (_, payload) => api._requests.Writer.TryWrite(payload), cancellationToken);
        api.LogListening(topics.ApiTopic, topics.ResponseTopic);
        return api;
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is { } subscription)
        {
            await subscription.DisposeAsync();
        }

        _requests.Writer.TryComplete();
        await _stop.CancelAsync();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
            // stopping
        }

        _stop.Dispose();
    }

    /// <summary>Runs one request message and returns the response message (exposed for tests).</summary>
    internal async Task<string> ExecuteAsync(string payload, CancellationToken cancellationToken)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            return new JsonObject { ["success"] = false, ["error"] = ex.Message }.ToJsonString();
        }

        var output = new CollectingOutput();
        using (var session = new RpcSession(output, "MQTT", isLocal: false, authorized: true, admin: false))
        {
            IReadOnlyList<JsonObject> messages = request switch
            {
                JsonObject one => [one],
                JsonArray many => many.OfType<JsonObject>().ToList(),
                _ => [],
            };
            foreach (var message in messages)
            {
                await _dispatcher.HandleAsync(session, Encoding.UTF8.GetBytes(message.ToJsonString()), cancellationToken);
            }
        }

        return output.ToJsonArray();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var payload in _requests.Reader.ReadAllAsync(cancellationToken))
        {
            if (payload.Length == 0)
            {
                continue;
            }

            try
            {
                var response = await ExecuteAsync(payload, cancellationToken);
                await _mqtt.PublishAsync(new MqttMessage(_responseTopic, response, Qos: 2), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                LogRequestFailed(ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT: JSON-RPC requests on {Topic}, replies on {ResponseTopic}")]
    private partial void LogListening(string topic, string responseTopic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: a JSON-RPC request failed: {Reason}")]
    private partial void LogRequestFailed(string reason);

    /// <summary>Everything the session sent while the request ran (replies and any events, as C++ collected them).</summary>
    private sealed class CollectingOutput : IRpcOutput
    {
        private readonly List<byte[]> _messages = [];

        public int Pending => 0;

        public void Send(JsonObject message) => SendRaw(RpcJson.ToUtf8(message));

        public void SendRaw(byte[] utf8Json)
        {
            lock (_messages)
            {
                _messages.Add(utf8Json);
            }
        }

        public string ToJsonArray()
        {
            lock (_messages)
            {
                var builder = new StringBuilder("[");
                for (var i = 0; i < _messages.Count; i++)
                {
                    builder.Append(i == 0 ? string.Empty : ",").Append(Encoding.UTF8.GetString(_messages[i]));
                }

                return builder.Append(']').ToString();
            }
        }
    }
}
