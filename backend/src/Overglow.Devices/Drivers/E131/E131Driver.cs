using System.Text.Json.Nodes;

namespace Overglow.Devices.Drivers.E131;

/// <summary>Settings of the "udpe131" driver (sources/led-drivers/schemas/schema-e131.json).</summary>
/// <param name="Cid">Component id; null generates a new one each time the driver starts (as in C++).</param>
public sealed record E131Options(string Host, int Port = E131Protocol.DefaultPort, int Universe = 1, bool DisableSplitting = false, Guid? Cid = null, string? SourceName = null)
{
    public static E131Options FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var cidText = DeviceConfig.GetString(config, "cid").Trim();
        Guid? cid = null;
        if (cidText.Length > 0)
        {
            cid = Guid.TryParse(cidText, out var parsed) && parsed != Guid.Empty
                ? parsed
                : throw new ArgumentException("The CID is not a valid UUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).", nameof(config));
        }

        var name = DeviceConfig.GetString(config, "source-name");
        return new E131Options(
            DeviceConfig.GetString(config, "host"),
            DeviceConfig.GetInt(config, "port", E131Protocol.DefaultPort),
            DeviceConfig.GetInt(config, "universe", 1),
            DeviceConfig.GetBool(config, "disableSplitting", false),
            cid,
            name.Length > 0 ? name : null);
    }
}

/// <summary>E1.31 / sACN over UDP. Port of <c>DriverNetUdpE131</c>.</summary>
public sealed class E131Driver : UdpLedDriver
{
    public const string TypeName = "udpe131";

    private readonly E131Options _options;
    private readonly byte[] _cid;
    private readonly byte[] _sourceName;
    private readonly byte[] _packet = new byte[E131Protocol.MaxPacketSize];
    private byte _sequence;

    public E131Driver(E131Options options, DriverContext context)
        : base(options?.Host ?? string.Empty, options?.Port ?? 0, context, "E1.31")
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _cid = (options.Cid ?? Guid.NewGuid()).ToByteArray(bigEndian: true);
        _sourceName = E131Protocol.SourceName(options.SourceName ?? $"Overglow on {Environment.MachineName}");
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new E131Driver(E131Options.FromConfig(config), context));

    public override string Type => TypeName;

    public override bool Write(ReadOnlySpan<byte> colors)
    {
        if (!Connected)
        {
            return false;
        }

        _sequence++;
        var ok = true;
        var packets = E131Protocol.PacketCount(colors.Length, _options.DisableSplitting);
        for (var i = 0; i < packets; i++)
        {
            var length = E131Protocol.WritePacket(colors, i, _options.Universe, _sequence, _options.DisableSplitting, _cid, _sourceName, _packet);
            ok &= Channel.Send(_packet.AsSpan(0, length));
        }

        return ok;
    }
}
