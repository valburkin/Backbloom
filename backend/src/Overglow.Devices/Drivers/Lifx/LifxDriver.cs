using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Overglow.Core.Color;
using Overglow.Devices.Drivers.Wiz;
using Overglow.Devices.Transport;

namespace Overglow.Devices.Drivers.Lifx;

/// <summary>Settings of the "lifx" driver (sources/led-drivers/schemas/schema-lifx.json).</summary>
/// <param name="TransitionMs">Fade time the lamps use for each color.</param>
public sealed record LifxOptions(IReadOnlyList<(NetworkLamp Lamp, byte[] Mac)> Lamps, int TransitionMs = 0, ColorOrder ColorOrder = ColorOrder.Rgb)
{
    public static LifxOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var lamps = new List<(NetworkLamp, byte[])>();
        foreach (var lamp in DeviceConfig.GetObjects(config, "lamps"))
        {
            var macText = DeviceConfig.GetString(lamp, "macAddress");
            if (LifxProtocol.ParseMac(macText) is { } mac && IPAddress.TryParse(DeviceConfig.GetString(lamp, "ipAddress"), out var address))
            {
                lamps.Add((new NetworkLamp(DeviceConfig.GetString(lamp, "name"), address, macText), mac));
            }
        }

        return new LifxOptions(lamps, DeviceConfig.GetInt(config, "transition", 0), ColorOrders.Parse(DeviceConfig.GetString(config, "colorOrder", "rgb")));
    }
}

/// <summary>
/// LIFX LAN protocol packets as built by <c>DriverNetLifx</c> (sources/led-drivers/net/DriverNetLifx.cpp): a 36-byte
/// little-endian header (size, protocol 1024 + addressable, source 0x12345678, target MAC) and SetColor (102, HSBK,
/// kelvin 6500), SetPower (21) or the GetService broadcast (2).
/// </summary>
public static partial class LifxProtocol
{
    public const int Port = 56700;
    public const int HeaderSize = 36;
    public const int SetColorSize = HeaderSize + 13;
    public const int SetPowerSize = HeaderSize + 2;
    public const ushort WhiteKelvin = 6500;

    /// <summary>"D0:73:D5:01:02:03" (or with '-') to six bytes; null when malformed.</summary>
    public static byte[]? ParseMac(string text)
    {
        if (!MacPattern().IsMatch(text ?? string.Empty))
        {
            return null;
        }

        return [.. text!.Split(':', '-').Select(p => byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
    }

    public static void WriteSetColor(Span<byte> packet, ReadOnlySpan<byte> mac, Vector3 rgb, ushort kelvin, uint durationMs)
    {
        WriteHeader(packet, SetColorSize, mac, 102);
        var hsv = Hsv.FromRgb(rgb);
        packet[36] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[37..], (ushort)(hsv.X / 360.0f * 65535.0f));
        BinaryPrimitives.WriteUInt16LittleEndian(packet[39..], (ushort)(hsv.Y * 65535.0f));
        BinaryPrimitives.WriteUInt16LittleEndian(packet[41..], (ushort)(hsv.Z * 65535.0f));
        BinaryPrimitives.WriteUInt16LittleEndian(packet[43..], kelvin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[45..], durationMs);
    }

    public static void WriteSetPower(Span<byte> packet, ReadOnlySpan<byte> mac, ushort level)
    {
        WriteHeader(packet, SetPowerSize, mac, 21);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[36..], level);
    }

    /// <summary>The GetService discovery broadcast (tagged, source 0x11, "LIFXV2" in the reserved bytes, flags 4).</summary>
    public static byte[] GetService()
    {
        var packet = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 1024 | (1 << 12) | (1 << 13));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x11);
        "LIFXV2"u8.CopyTo(packet.AsSpan(16));
        packet[22] = 0x04;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(32), 2);
        return packet;
    }

    private static void WriteHeader(Span<byte> packet, int size, ReadOnlySpan<byte> mac, ushort type)
    {
        packet[..size].Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[2..], 1024 | (1 << 12));
        BinaryPrimitives.WriteUInt32LittleEndian(packet[4..], 0x12345678);
        mac[..6].CopyTo(packet[8..]);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[32..], type);
    }

    [GeneratedRegex("^([0-9A-Fa-f]{2}([-:])){5}([0-9A-Fa-f]{2})$")]
    private static partial Regex MacPattern();
}

/// <summary>LIFX lamps over UDP, fed with the unrounded colors (one lamp per LED).</summary>
public sealed class LifxDriver : IFloatColorDriver
{
    public const string TypeName = "lifx";

    private readonly LifxOptions _options;
    private readonly IDatagramChannel[] _channels;
    private readonly byte[] _packet = new byte[LifxProtocol.SetColorSize];
    private bool _connected;

    public LifxDriver(LifxOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (options.Lamps.Count == 0)
        {
            throw new ArgumentException("No LIFX lamp with a valid IP and MAC address is configured.", nameof(options));
        }

        _options = options;
        _channels = [.. options.Lamps.Select(_ => context.CreateDatagramChannel())];
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new LifxDriver(LifxOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
        Identify = IdentifyAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy => RetryPolicy.Default;

    public string? Status => null;

    public bool WritesFloatColors => true;

    /// <summary>Broadcasts GetService and lists every lamp that answers (StateService) within 3 s, once per MAC.</summary>
    public static async Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var replies = await request.Context.Discovery.Broadcast.ProbeAsync(LifxProtocol.GetService(), LifxProtocol.Port, TimeSpan.FromSeconds(3), request.CancellationToken);
        var lamps = new JsonArray();
        var macs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reply in replies)
        {
            if (reply.Data.Length < LifxProtocol.HeaderSize || BinaryPrimitives.ReadUInt16LittleEndian(reply.Data.AsSpan(32)) != 3)
            {
                continue;
            }

            var mac = string.Join(':', reply.Data.Skip(8).Take(6).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            if (macs.Add(mac))
            {
                var ip = reply.From.Address.ToString();
                lamps.Add((JsonNode)new JsonObject { ["name"] = $"Lamp #{lamps.Count}", ["ipAddress"] = ip, ["macAddress"] = mac, ["value"] = ip });
            }
        }

        return lamps;
    }

    /// <summary>Power on, blue for 250 ms, then fade to black over 2 s.</summary>
    public static async Task IdentifyAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = request.Parameters;
        if (p["name"] is null || !IPAddress.TryParse(DeviceConfig.GetString(p, "ipAddress"), out var address) ||
            LifxProtocol.ParseMac(DeviceConfig.GetString(p, "macAddress")) is not { } mac)
        {
            return;
        }

        using var channel = request.Context.CreateDatagramChannel();
        channel.Connect(new IPEndPoint(address, LifxProtocol.Port));
        var packet = new byte[LifxProtocol.SetColorSize];
        LifxProtocol.WriteSetPower(packet, mac, 0xFFFF);
        channel.Send(packet.AsSpan(0, LifxProtocol.SetPowerSize));
        LifxProtocol.WriteSetColor(packet, mac, new Vector3(0, 0, 1), LifxProtocol.WhiteKelvin, 250);
        channel.Send(packet);
        await Task.Delay(TimeSpan.FromMilliseconds(250), request.Context.Time, request.CancellationToken);
        LifxProtocol.WriteSetColor(packet, mac, Vector3.Zero, LifxProtocol.WhiteKelvin, 2000);
        channel.Send(packet);
    }

    public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _channels.Length; i++)
        {
            _channels[i].Connect(new IPEndPoint(_options.Lamps[i].Lamp.Address, LifxProtocol.Port));
        }

        _connected = true;
        SetPower(0xFFFF);
        return ValueTask.CompletedTask;
    }

    public bool Write(ReadOnlySpan<byte> colors) => false;

    public bool WriteFloat(ReadOnlySpan<Vector3> colors, TimeSpan frameInterval)
    {
        if (!_connected)
        {
            return false;
        }

        var ok = true;
        var n = Math.Min(colors.Length, _channels.Length);
        for (var i = 0; i < n; i++)
        {
            LifxProtocol.WriteSetColor(_packet, _options.Lamps[i].Mac, FloatColorOrder.Apply(colors[i], _options.ColorOrder), LifxProtocol.WhiteKelvin, (uint)_options.TransitionMs);
            ok &= _channels[i].Send(_packet);
        }

        return ok;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            SetPower(0);
        }

        _connected = false;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }
    }

    private void SetPower(ushort level)
    {
        for (var i = 0; i < _channels.Length; i++)
        {
            LifxProtocol.WriteSetPower(_packet, _options.Lamps[i].Mac, level);
            _channels[i].Send(_packet.AsSpan(0, LifxProtocol.SetPowerSize));
        }
    }
}
