using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MQTTnet.Server;
using Backbloom.Mqtt;

namespace Backbloom.Devices.Tests;

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

    private static MqttServer Broker(int port) =>
        new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointBoundIPV6Address(IPAddress.None).WithDefaultEndpointPort(port).Build());

    [Fact]
    public async Task Connection_PublishesSubscribesAndSendsTheShutdownMessages()
    {
        var port = FreePort();
        using var broker = Broker(port);
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

    [Fact]
    public void Settings_WithoutASection_AreDisabled_AndNeverShowThePassword()
    {
        Assert.Equal(MqttSettings.Disabled, MqttSettings.FromJson(null));
        Assert.Equal(new MqttSettings(false, string.Empty), MqttSettings.FromJson(Ctx.Json("""{ "host": 5, "port": "x" }""")));

        Assert.Equal("broker.local:1883 (TLS: False, user: none)", new MqttSettings(true, "broker.local", Password: "secret").ToString());
        Assert.DoesNotContain("secret", new MqttSettings(true, "broker.local", 1883, "u", "secret").ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A broker that isn't up yet: the connection tries again every 5 seconds (on the clock it's given), at most
    /// <c>maxRetry</c> times, and restores its subscriptions once it gets through.
    /// </summary>
    [Fact]
    public async Task Connection_RetriesUntilTheBrokerIsUp_AndRestoresTheSubscriptions()
    {
        var port = FreePort();
        var time = new FakeTimeProvider();
        var log = new RecordingLogger<MqttConnection>();
        var connection = new MqttConnection(log, time);
        var connects = 0;
        connection.Connected += () => Interlocked.Increment(ref connects);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await connection.SubscribeAsync("backbloom/test", (_, payload) => received.TrySetResult(payload), TestContext.Current.CancellationToken);

        await connection.ConfigureAsync(new MqttSettings(true, "127.0.0.1", port, MaxRetry: 3), TestContext.Current.CancellationToken);
        Assert.False(connection.IsConnected);

        using var broker = Broker(port);
        await broker.StartAsync();
        // connected once the subscriptions are restored
        Wait.Until(() =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            return Volatile.Read(ref connects) > 0;
        }, timeoutMs: 10_000);

        Assert.Equal(1, connects);
        Assert.Contains(log.Messages, m => m.StartsWith("MQTT: reconnecting (1/3)", StringComparison.Ordinal));
        Assert.True(await connection.PublishAsync("backbloom/test", "back", TestContext.Current.CancellationToken));
        Assert.Equal("back", await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await connection.DisposeAsync();
        await broker.StopAsync();
    }

    [Fact]
    public async Task Connection_GivesUpAfterMaxRetry()
    {
        var time = new FakeTimeProvider();
        var log = new RecordingLogger<MqttConnection>();
        var connection = new MqttConnection(log, time);

        await connection.ConfigureAsync(new MqttSettings(true, "127.0.0.1", FreePort(), MaxRetry: 2), TestContext.Current.CancellationToken);

        // the first attempt and two retries fail
        Wait.Until(() =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            return log.Count("MQTT: couldn't connect") == 3;
        }, timeoutMs: 10_000);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, log.Count("MQTT: reconnecting"));
        Assert.Equal(3, log.Count("MQTT: couldn't connect"));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Connection_ReconnectsWhenTheBrokerComesBack()
    {
        var port = FreePort();
        var time = new FakeTimeProvider();
        var connection = new MqttConnection(new RecordingLogger<MqttConnection>(), time);
        using (var broker = Broker(port))
        {
            await broker.StartAsync();
            await connection.ConfigureAsync(new MqttSettings(true, "127.0.0.1", port, MaxRetry: 5), TestContext.Current.CancellationToken);
            Assert.True(connection.IsConnected);
            await broker.StopAsync();
        }

        Wait.Until(() => !connection.IsConnected);
        using var restarted = Broker(port);
        await restarted.StartAsync();
        Wait.Until(() =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            return connection.IsConnected;
        }, timeoutMs: 10_000);

        // unchanged settings keep the connection; a removed owner's shutdown messages aren't sent
        await connection.ConfigureAsync(new MqttSettings(true, "127.0.0.1", port, MaxRetry: 5), TestContext.Current.CancellationToken);
        Assert.True(connection.IsConnected);
        var seen = new ConcurrentQueue<string>();
        restarted.InterceptingPublishAsync += e =>
        {
            seen.Enqueue(e.ApplicationMessage.Topic);
            return Task.CompletedTask;
        };
        connection.SetShutdownMessages("lamps", [new("lamps/off", "1")]);
        connection.SetShutdownMessages("lamps", null);
        connection.SetShutdownMessages("tv", [new("tv/off", "1")]);
        await connection.DisposeAsync();
        await connection.DisposeAsync();

        Wait.Until(() => seen.Contains("tv/off"));
        Assert.DoesNotContain("lamps/off", seen);
        await restarted.StopAsync();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public int Count(string prefix) => _messages.Count(m => m.StartsWith(prefix, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }
}
