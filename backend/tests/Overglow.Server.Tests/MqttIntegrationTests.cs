using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Server;
using Overglow.Core.Color;
using Overglow.Core.Priorities;
using Overglow.Engine;
using Overglow.Server.Mqtt;

namespace Overglow.Server.Tests;

/// <summary>The MQTT integration of the real host against an in-process broker (MQTTnet.Server) on loopback.</summary>
public sealed class MqttIntegrationTests : IAsyncLifetime
{
    private readonly int _port = FreePort();
    private readonly ConcurrentQueue<(string Topic, string Payload)> _received = new();
    private MqttServer _broker = null!;
    private IMqttClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(_port).Build());
        await _broker.StartAsync();
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            _received.Enqueue((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty));
            return Task.CompletedTask;
        };
        await _client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).WithClientId("test-client").Build(), TestContext.Current.CancellationToken);
        await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("#").Build(), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _broker.StopAsync();
        _broker.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private Task<TestServer> StartAsync(string extra = "") =>
        TestServer.StartAsync($$"""{ "mqtt": { "enable": true, "host": "127.0.0.1", "port": {{_port}} {{extra}} } }""");

    /// <summary>The first message on <paramref name="topic"/> (a trailing "/#" matches the subtopics) that matches.</summary>
    private (string Topic, string Payload) Next(string topic, Func<string, bool>? match = null)
    {
        bool Topic(string t) => topic.EndsWith("/#", StringComparison.Ordinal) ? t.StartsWith(topic[..^1], StringComparison.Ordinal) : t == topic;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var m in _received)
            {
                if (Topic(m.Topic) && (match is null || match(m.Payload)))
                {
                    return m;
                }
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException($"No message on {topic}.");
    }

    /// <summary>What a client that subscribes now gets as the retained message of <paramref name="topic"/>.</summary>
    private async Task<string?> RetainedAsync(string topic)
    {
        using var late = new MqttClientFactory().CreateMqttClient();
        var retained = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        late.ApplicationMessageReceivedAsync += e =>
        {
            if (e.ApplicationMessage.Retain)
            {
                retained.TrySetResult(e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty);
            }

            return Task.CompletedTask;
        };
        await late.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).WithClientId("late-client").Build(), TestContext.Current.CancellationToken);
        await late.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(topic).Build(), TestContext.Current.CancellationToken);
        try
        {
            return await retained.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            Thread.Sleep(10);
        }
    }

    private async Task PublishAsync(string topic, string payload) =>
        await _client.PublishStringAsync(topic, payload, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task JsonApi_AnswersOneRequestOrAnArray_OnTheResponseTopic()
    {
        await using var server = await StartAsync();
        Next("Overglow/status", p => p == "online");

        await PublishAsync("Overglow/JsonAPI", """{ "command": "sysinfo", "tan": 7 }""");
        var one = JsonNode.Parse(Next("Overglow/JsonAPI/response").Payload)!.AsArray();
        Assert.Single(one);
        Assert.Equal("sysinfo", one[0]!["command"]!.GetValue<string>());
        Assert.Equal(7, one[0]!["tan"]!.GetValue<int>());
        Assert.True(one[0]!["success"]!.GetValue<bool>());

        _received.Clear();
        await PublishAsync("Overglow/JsonAPI", """
            [ { "command": "componentstate", "componentstate": { "component": "LEDDEVICE", "state": false }, "tan": 1 },
              { "command": "color", "color": [0, 0, 255], "priority": 50, "tan": 2 } ]
            """);
        var two = JsonNode.Parse(Next("Overglow/JsonAPI/response").Payload)!.AsArray();
        Assert.Equal([1, 2], two.Select(r => r!["tan"]!.GetValue<int>()));
        Assert.All(two, r => Assert.True(r!["success"]!.GetValue<bool>()));
        var engine = server.Services.GetRequiredService<LightingEngine>();
        Assert.False(engine.Components.IsOn(Component.LedDevice));
        Assert.Equal(50, engine.Sources.Visible.Priority);
    }

    [Fact]
    public async Task JsonApi_HasTheRightsOfALocalClient()
    {
        await using var server = await StartAsync();
        Next("Overglow/status", p => p == "online");
        await PublishAsync("Overglow/JsonAPI", """{ "command": "config", "subcommand": "getconfig", "tan": 3 }""");
        var reply = JsonNode.Parse(Next("Overglow/JsonAPI/response").Payload)!.AsArray().Single()!;
        Assert.Equal("No Authorization", reply["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task JsonApi_ReportsUnreadableMessages()
    {
        await using var server = await StartAsync();
        Next("Overglow/status", p => p == "online");
        await PublishAsync("Overglow/JsonAPI", "{ not json");
        var reply = JsonNode.Parse(Next("Overglow/JsonAPI/response").Payload)!.AsObject();
        Assert.False(reply["success"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(reply["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task CustomTopic_AndDisabledApiAccess()
    {
        await using var server = await StartAsync(""", "custom_topic": " Living/Ambilight ", "disableApiAccess": true""");
        Next("Living/Ambilight/status", p => p == "online");
        await PublishAsync("Living/Ambilight/JsonAPI", """{ "command": "sysinfo", "tan": 1 }""");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(_received, m => m.Topic.EndsWith("/response", StringComparison.Ordinal));
        Assert.Null(server.Services.GetRequiredService<MqttIntegration>().Api);
    }

    [Fact]
    public async Task Availability_IsOnlineWhileRunning_AndOfflineAfter()
    {
        var server = await StartAsync();
        Next("Overglow/status", p => p == "online");
        Assert.Equal("online", await RetainedAsync("Overglow/status"));
        await server.DisposeAsync();
        Next("Overglow/status", p => p == "offline");
        Assert.Equal("offline", await RetainedAsync("Overglow/status"));
    }

    [Fact]
    public async Task HomeAssistant_DiscoversALight_AndControlsIt()
    {
        await using var server = await StartAsync();
        var config = Next("homeassistant/light/#");
        Assert.Equal(config.Payload, await RetainedAsync(config.Topic));
        var discovery = JsonNode.Parse(config.Payload)!.AsObject();
        Assert.Equal("json", discovery["schema"]!.GetValue<string>());
        Assert.Equal("Overglow/light/set", discovery["command_topic"]!.GetValue<string>());
        Assert.Equal("Overglow/light/state", discovery["state_topic"]!.GetValue<string>());
        Assert.Equal("Overglow/status", discovery["availability_topic"]!.GetValue<string>());
        Assert.Equal(["rgb"], discovery["supported_color_modes"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Equal(
            [HomeAssistantLight.ScreenEffect, .. EffectEngine.Available.Select(e => e.Name)],
            discovery["effect_list"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.StartsWith("overglow_", discovery["unique_id"]!.GetValue<string>(), StringComparison.Ordinal);

        // the screen becomes visible with its first frame, which may come after the first state was published
        var state = JsonNode.Parse(Next("Overglow/light/state", p => p.Contains(HomeAssistantLight.ScreenEffect, StringComparison.Ordinal)).Payload)!;
        Assert.Equal("ON", state["state"]!.GetValue<string>());
        Assert.Equal(HomeAssistantLight.ScreenEffect, state["effect"]!.GetValue<string>());

        var engine = server.Services.GetRequiredService<LightingEngine>();
        await PublishAsync("Overglow/light/set", """{ "state": "OFF" }""");
        Until(() => !engine.Components.IsOn(Component.All));
        Next("Overglow/light/state", p => p.Contains("\"state\":\"OFF\"", StringComparison.Ordinal));

        await PublishAsync("Overglow/light/set", """{ "state": "ON", "color": { "r": 0, "g": 255, "b": 0 }, "brightness": 128 }""");
        Until(() => engine.Components.IsOn(Component.All) && engine.Sources.Visible.Component == SourceComponent.Color);
        Assert.Equal(new Rgb8(0, 128, 0), engine.Sources.Visible.Color);
        var colored = JsonNode.Parse(Next("Overglow/light/state", p => p.Contains("\"r\":0,\"g\":255", StringComparison.Ordinal)).Payload)!;
        Assert.Equal(128, colored["brightness"]!.GetValue<int>());
        Assert.Null(colored["effect"]);

        await PublishAsync("Overglow/light/set", """{ "state": "ON", "brightness": 255 }""");
        Until(() => engine.Sources.Visible.Color == new Rgb8(0, 255, 0));

        await PublishAsync("Overglow/light/set", $$"""{ "state": "ON", "effect": "{{HomeAssistantLight.ScreenEffect}}" }""");
        Until(() => engine.Sources.Visible.Component == SourceComponent.SystemGrabber);
        Next("Overglow/light/state", p => p.Contains($"\"effect\":\"{HomeAssistantLight.ScreenEffect}\"", StringComparison.Ordinal));

        await PublishAsync("Overglow/light/set", """{ "state": "ON", "effect": "Breath" }""");
        Until(() => (engine.Sources.Visible.Renderer as EffectSource)?.Definition.Name == "Breath");
        Assert.Equal(HomeAssistantLight.ColorPriority, engine.Sources.Visible.Priority);
        Next("Overglow/light/state", p => p.Contains("\"effect\":\"Breath\"", StringComparison.Ordinal));

        await PublishAsync("Overglow/light/set", """{ "state": "ON", "effect": "Music: fullscreen pulse (MULTI COLOR)" }""");
        Until(() => (engine.Sources.Visible.Renderer as EffectSource)?.Definition.IsMusic == true);
    }

    [Fact]
    public async Task HomeAssistant_Off_RemovesTheEntity()
    {
        await using var server = await StartAsync(""", "homeAssistantDiscovery": false""");
        var removal = Next("homeassistant/light/#");
        Assert.Equal(string.Empty, removal.Payload);
        Assert.Null(server.Services.GetRequiredService<MqttIntegration>().Light);
    }

    [Fact]
    public void Settings_DefaultTopicAndFlags()
    {
        Assert.Equal(new MqttIntegrationSettings("Overglow", true, true), MqttIntegrationSettings.FromJson(null));
        Assert.Equal(
            new MqttIntegrationSettings("Room", false, false),
            MqttIntegrationSettings.FromJson(JsonNode.Parse("""{ "custom_topic": " Room ", "disableApiAccess": true, "homeAssistantDiscovery": false }""")!.AsObject()));
        Assert.Equal("Room/JsonAPI/response", new MqttIntegrationSettings("Room", true, true).ResponseTopic);
    }
}
