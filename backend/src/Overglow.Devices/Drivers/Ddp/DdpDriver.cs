using System.Net;
using System.Text.Json.Nodes;
using Overglow.Devices.Transport;

namespace Overglow.Devices.Drivers.Ddp;

/// <summary>Settings of the "ddp" driver (sources/led-drivers/schemas/schema-ddp.json; RGBW options come later).</summary>
public sealed record DdpOptions(string Host, int Port = DdpProtocol.DefaultPort)
{
    public static DdpOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new DdpOptions(DeviceConfig.GetString(config, "host"), DeviceConfig.GetInt(config, "port", DdpProtocol.DefaultPort));
    }
}

/// <summary>DDP over UDP (WLED, ESPixelStick, xLights...). Port of <c>DriverNetDDP</c>, RGB only.</summary>
public sealed class DdpDriver : ILedDriver
{
    public const string TypeName = "ddp";

    private readonly DdpOptions _options;
    private readonly IDatagramChannel _udp;
    private readonly Func<string, CancellationToken, ValueTask<IPAddress>> _resolve;
    private readonly byte[] _packet = new byte[DdpProtocol.MaxPacketSize];
    private byte _sequence;
    private bool _connected;

    public DdpDriver(DdpOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("No DDP target host is configured.", nameof(options));
        }

        if (options.Port is <= 0 or > 65535)
        {
            throw new ArgumentException($"Invalid DDP port {options.Port}.", nameof(options));
        }

        _options = options;
        _udp = context.CreateDatagramChannel();
        _resolve = context.ResolveHost;
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new DdpDriver(DdpOptions.FromConfig(config), context));

    public string Type => TypeName;

    public RetryPolicy RetryPolicy { get; } = RetryPolicy.Default;

    public string? Status => null;

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        var address = await _resolve(_options.Host, cancellationToken);
        _udp.Connect(new IPEndPoint(address, _options.Port));
        _connected = true;
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        if (!_connected)
        {
            return false;
        }

        _sequence = DdpProtocol.NextSequence(_sequence);
        var ok = true;
        var packets = DdpProtocol.PacketCount(colors.Length / 3);
        for (var i = 0; i < packets; i++)
        {
            var length = DdpProtocol.WriteFrame(colors, i, _sequence, _packet);
            ok &= _udp.Send(_packet.AsSpan(0, length));
        }

        return ok;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _udp.Dispose();
}
