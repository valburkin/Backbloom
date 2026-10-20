using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet.Server;
using Overglow.Mqtt;

namespace Overglow.Devices.Tests;

/// <summary>The MQTTnet-based connection against an in-process broker (MQTTnet.Server) on loopback.</summary>
public sealed class MqttConnectionTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Connection_PublishesSubscribesAndSendsTheShutdownMessages()
    {
        var port = FreePort();
        using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.None).WithDefaultEndpointPort(port).Build());
        var seen = new ConcurrentQueue<(string Topic, string Payload)>();
        broker.InterceptingPublishAsync += e =>
        {
            seen.Enqueue((e.ApplicationMessage.Topic, Encoding.UTF8.GetString(e.ApplicationMessage.Payload)));
            return Task.CompletedTask;
        };
        await broker.StartAsync();

        var connection = new MqttConnection(NullLogger<MqttConnection>.Instance);
        await connection.ConfigureAsync(new MqttSettings(true, "127.0.0.1", port), TestContext.Current.CancellationToken);
        Assert.True(connection.IsConnected);

        var received = new TaskCompletionSource<string>();
        await using (await connection.SubscribeAsync("zigbee2mqtt/bridge/#", (_, payload) => received.TrySetResult(payload), TestContext.Current.CancellationToken))
        {
            Assert.True(await connection.PublishAsync("zigbee2mqtt/bridge/devices", "[]", TestContext.Current.CancellationToken));
            Assert.Equal("[]", await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }

        connection.SetShutdownMessages("lamps", [new("zigbee2mqtt/Desk/set", """{"state":"OFF"}""")]);
        await connection.DisposeAsync();

        Wait.Until(() => seen.Any(m => m.Topic == "zigbee2mqtt/Desk/set"));
        Assert.Contains(("zigbee2mqtt/Desk/set", """{"state":"OFF"}"""), seen);
        await broker.StopAsync();
    }

    [Fact]
    public async Task Connection_StaysOffWhenDisabledOrUnreachable()
    {
        var connection = new MqttConnection(NullLogger<MqttConnection>.Instance);
        await connection.ConfigureAsync(MqttSettings.Disabled, TestContext.Current.CancellationToken);
        Assert.False(connection.IsConnected);
        Assert.False(await connection.PublishAsync("t", "p", TestContext.Current.CancellationToken));

        await connection.ConfigureAsync(new MqttSettings(true, "127.0.0.1", FreePort()), TestContext.Current.CancellationToken);
        Assert.False(connection.IsConnected);
        await connection.DisposeAsync();
    }

    [Fact]
    public void Settings_ReadTheMqttSection() =>
        Assert.Equal(
            new MqttSettings(true, "broker.local", 8883, "u", "p", true, false, 5),
            MqttSettings.FromJson(Ctx.Json("""{ "enable": true, "host": "broker.local", "port": 8883, "username": "u", "password": "p", "is_ssl": true, "ignore_ssl_errors": false, "maxRetry": 5 }""")));
}
