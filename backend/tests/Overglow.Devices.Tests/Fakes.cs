using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Devices.Transport;
using Overglow.Discovery;
using Overglow.Discovery.Mdns;
using Overglow.Discovery.Ssdp;
using Overglow.Discovery.Usb;
using Overglow.Mqtt;

namespace Overglow.Devices.Tests;

/// <summary>A serial port that records what is written and plays back scripted input.</summary>
internal sealed class FakeSerialPort(string name) : ISerialPort
{
    private readonly ConcurrentQueue<byte[]> _input = new();

    public string PortName { get; } = name;

    public bool IsOpen { get; private set; }

    public bool DtrEnable { get; set; }

    public bool RtsEnable { get; set; }

    public List<string> Signals { get; } = [];

    public List<byte[]> Writes { get; } = [];

    public Func<byte[], Exception?>? FailWrite { get; set; }

    public int BytesToRead => _input.Sum(b => b.Length);

    public void Feed(byte[] data) => _input.Enqueue(data);

    public void Open() => IsOpen = true;

    public void Write(ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray();
        if (FailWrite?.Invoke(bytes) is { } ex)
        {
            throw ex;
        }

        Writes.Add(bytes);
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (!_input.TryDequeue(out var data))
        {
            Thread.Sleep(Math.Min(10, (int)timeout.TotalMilliseconds));
            return 0;
        }

        data.CopyTo(buffer);
        return data.Length;
    }

    public void Flush()
    {
    }

    public void Close() => IsOpen = false;

    public void Dispose() => IsOpen = false;
}

internal sealed class FakeSerialFactory : ISerialPortFactory
{
    public List<FakeSerialPort> Created { get; } = [];

    public Action<FakeSerialPort>? OnCreate { get; set; }

    public ISerialPort Create(string portName, int baudRate)
    {
        var port = new FakeSerialPort(portName);
        OnCreate?.Invoke(port);
        Created.Add(port);
        return port;
    }
}

internal sealed class FakeSerialEnumerator(params SerialPortInfo[] ports) : ISerialPortEnumerator
{
    public IReadOnlyList<SerialPortInfo> GetPorts() => ports;
}

internal sealed class FakeHidEnumerator(params HidDeviceInfo[] devices) : IHidEnumerator
{
    public IReadOnlyList<HidDeviceInfo> GetDevices() => devices;
}

/// <summary>A HID device that records output reports and answers reads from a queue.</summary>
internal sealed class FakeHidDevice : IHidDevice
{
    public List<byte[]> Reports { get; } = [];

    public ConcurrentQueue<byte[]> Input { get; } = new();

    public Func<byte[], byte[]?>? Answer { get; set; }

    public bool Disposed { get; private set; }

    public bool Write(ReadOnlySpan<byte> report)
    {
        var bytes = report.ToArray();
        Reports.Add(bytes);
        if (Answer?.Invoke(bytes) is { } reply)
        {
            Input.Enqueue(reply);
        }

        return true;
    }

    public int Read(Span<byte> report, TimeSpan timeout)
    {
        if (!Input.TryDequeue(out var data))
        {
            return 0;
        }

        data.CopyTo(report);
        return data.Length;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeHidOpener(FakeHidDevice device) : IHidOpener
{
    public List<string> Opened { get; } = [];

    public IHidDevice Open(string path)
    {
        Opened.Add(path);
        return device;
    }
}

internal sealed class FakeMdns(params MdnsServiceInstance[] instances) : IMdnsBrowser
{
    public List<string> Browsed { get; } = [];

    public Task<IReadOnlyList<MdnsServiceInstance>> BrowseAsync(string serviceType, TimeSpan wait, CancellationToken cancellationToken)
    {
        Browsed.Add(serviceType);
        return Task.FromResult<IReadOnlyList<MdnsServiceInstance>>([.. instances.Where(i => i.ServiceType == serviceType)]);
    }
}

internal sealed class FakeSsdp(params SsdpResponse[] responses) : ISsdpSearcher
{
    public List<(string Target, int Port)> Searches { get; } = [];

    public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Searches.Add((searchTarget, port));
        return Task.FromResult<IReadOnlyList<SsdpResponse>>(responses);
    }
}

internal sealed class FakeProbe(params UdpProbeReply[] replies) : IUdpProbe
{
    public List<(byte[] Request, int Port)> Probes { get; } = [];

    public Task<IReadOnlyList<UdpProbeReply>> ProbeAsync(byte[] request, int port, TimeSpan duration, CancellationToken cancellationToken)
    {
        Probes.Add((request, port));
        return Task.FromResult<IReadOnlyList<UdpProbeReply>>(replies);
    }
}

/// <summary>Records publishes and delivers messages to subscribers.</summary>
internal sealed class FakeMqtt : IMqttConnection
{
    private readonly List<(string Topic, Action<string, string> Handler)> _subscribers = [];

    public bool IsConnected { get; set; } = true;

    public List<(string Topic, string Payload)> Published { get; } = [];

    public Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>> Shutdown { get; } = [];

    public Dictionary<string, string> Retained { get; } = [];

    public Task<bool> PublishAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        lock (Published)
        {
            Published.Add((topic, payload));
        }

        return Task.FromResult(IsConnected);
    }

    public Task<IAsyncDisposable> SubscribeAsync(string topic, Action<string, string> received, CancellationToken cancellationToken)
    {
        _subscribers.Add((topic, received));
        if (Retained.TryGetValue(topic, out var payload))
        {
            received(topic, payload);
        }

        return Task.FromResult<IAsyncDisposable>(new Unsubscribe(() => _subscribers.Remove((topic, received))));
    }

    public void SetShutdownMessages(string owner, IReadOnlyList<KeyValuePair<string, string>>? messages)
    {
        if (messages is { Count: > 0 })
        {
            Shutdown[owner] = messages;
        }
        else
        {
            Shutdown.Remove(owner);
        }
    }

    private sealed class Unsubscribe(Action action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            action();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Hands out a new recording channel per driver socket.</summary>
internal sealed class ChannelFactory
{
    public List<RecordingChannel> Channels { get; } = [];

    public IDatagramChannel Create()
    {
        var channel = new RecordingChannel();
        Channels.Add(channel);
        return channel;
    }
}

internal static class Ctx
{
    public static DriverContext Create(
        HttpMessageHandler? http = null,
        Func<IDatagramChannel>? channels = null,
        ISerialPortFactory? serial = null,
        IHidOpener? hid = null,
        DiscoveryServices? discovery = null,
        IMqttConnection? mqtt = null,
        string resolveTo = "192.0.2.10") =>
        new(NullLoggerFactory.Instance, new HttpClient(http ?? new FakeHttpHandler((_, _) => FakeHttpHandler.Json("{}"))), channels ?? (() => new RecordingChannel()), (_, _) => ValueTask.FromResult(IPAddress.Parse(resolveTo)))
        {
            SerialPorts = serial ?? new FakeSerialFactory(),
            Hid = hid ?? new FakeHidOpener(new FakeHidDevice()),
            Discovery = discovery ?? Discovery(),
            Mqtt = mqtt,
        };

    public static DiscoveryServices Discovery(
        IMdnsBrowser? mdns = null, ISsdpSearcher? ssdp = null, ISerialPortEnumerator? serial = null, IHidEnumerator? hid = null, IUdpProbe? probe = null) =>
        new(mdns ?? new FakeMdns(), ssdp ?? new FakeSsdp(), serial ?? new FakeSerialEnumerator(), hid ?? new FakeHidEnumerator(), probe ?? new FakeProbe());

    public static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    public static DeviceRequest Request(DriverContext context, string parameters = "{}") => new(Json(parameters), context, CancellationToken.None);
}
