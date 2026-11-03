using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backbloom.Devices.Transport;

namespace Backbloom.Devices.Drivers.Govee;

/// <summary>A light's state from <c>devStatus</c>.</summary>
/// <param name="Kelvin">The color temperature when the light shows white by temperature, else 0.</param>
public sealed record GoveeStatus(bool On, int Brightness, (byte R, byte G, byte B)? Color, int Kelvin);

/// <summary>One configured Govee light.</summary>
/// <param name="DeviceId">Govee's device id ("1F:80:C5:32:32:36:72:4E"), as the scan reports it; may be empty.</param>
/// <param name="Sku">The model ("H6199"); may be empty.</param>
public sealed record GoveeLamp(string Name, IPAddress Address, string DeviceId, string Sku);

/// <summary>Settings of the "govee" driver (Backbloom's own, Schemas/govee.json).</summary>
/// <param name="Brightness">Brightness in percent (1..100), set when the lights are switched on.</param>
/// <param name="UpdateInterval">The shortest time between two colors sent to a light (Govee lights drop commands that come faster).</param>
/// <param name="CommandSpacing">The time between the commands that switch a light on (200 ms; tests use 0).</param>
public sealed record GoveeOptions(IReadOnlyList<GoveeLamp> Lamps, int Brightness = 100, TimeSpan? UpdateInterval = null, TimeSpan? CommandSpacing = null)
{
    public TimeSpan Interval => UpdateInterval ?? GoveeDriver.DefaultUpdateInterval;

    public TimeSpan Spacing => CommandSpacing ?? GoveeDriver.DefaultCommandSpacing;

    public static GoveeOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var lamps = new List<GoveeLamp>();
        foreach (var lamp in DeviceConfig.GetObjects(config, "lamps"))
        {
            if (IPAddress.TryParse(DeviceConfig.GetString(lamp, "ipAddress"), out var address))
            {
                lamps.Add(new GoveeLamp(DeviceConfig.GetString(lamp, "name"), address, DeviceConfig.GetString(lamp, "deviceId"), DeviceConfig.GetString(lamp, "sku")));
            }
        }

        return new GoveeOptions(
            lamps,
            Math.Clamp(DeviceConfig.GetInt(config, "brightness", 100), 1, 100),
            TimeSpan.FromMilliseconds(Math.Clamp(DeviceConfig.GetInt(config, "updateInterval", (int)GoveeDriver.DefaultUpdateInterval.TotalMilliseconds), 50, 2000)));
    }
}

/// <summary>
/// The Govee LAN API's messages (Govee's "LAN API" guide): JSON <c>{"msg":{"cmd":..., "data":{...}}}</c> datagrams,
/// the scan to 239.255.255.250:4001 (answers to port 4002 of the querier), commands to the light's port 4003.
/// </summary>
public static class GoveeProtocol
{
    public const int ScanPort = 4001;
    public const int ReplyPort = 4002;
    public const int ControlPort = 4003;

    /// <summary>The longest <see cref="WriteColor"/> datagram.</summary>
    public const int MaxColorLength = 96;

    public static readonly IPAddress ScanGroup = IPAddress.Parse("239.255.255.250");

    public static byte[] Scan => """{"msg":{"cmd":"scan","data":{"account_topic":"reserve"}}}"""u8.ToArray();

    /// <summary>Asks a light for its state; it answers at port 4002 of the asker.</summary>
    public static byte[] DevStatus => """{"msg":{"cmd":"devStatus","data":{}}}"""u8.ToArray();

    /// <summary>White by color temperature (<c>colorwc</c> with a black color and the kelvin).</summary>
    public static byte[] ColorTemperature(int kelvin) =>
        Encoding.UTF8.GetBytes("""{"msg":{"cmd":"colorwc","data":{"color":{"r":0,"g":0,"b":0},"colorTemInKelvin":""" + kelvin.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}");

    /// <summary>A <c>devStatus</c> answer: on, brightness (1..100), color and color temperature (0: a color); null for anything else.</summary>
    public static GoveeStatus? ReadStatus(byte[] datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        JsonObject? msg;
        try
        {
            msg = (JsonNode.Parse(datagram) as JsonObject)?["msg"] as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (msg is null || DeviceConfig.GetString(msg, "cmd") != "devStatus" || msg["data"] is not JsonObject d || d["onOff"] is null)
        {
            return null;
        }

        var color = d["color"] as JsonObject;
        return new GoveeStatus(
            DeviceConfig.GetInt(d, "onOff", 0) == 1,
            Math.Clamp(DeviceConfig.GetInt(d, "brightness", 100), 1, 100),
            color is null ? null : ((byte)Math.Clamp(DeviceConfig.GetInt(color, "r", 0), 0, 255), (byte)Math.Clamp(DeviceConfig.GetInt(color, "g", 0), 0, 255), (byte)Math.Clamp(DeviceConfig.GetInt(color, "b", 0), 0, 255)),
            DeviceConfig.GetInt(d, "colorTemInKelvin", 0));
    }

    public static byte[] Turn(bool on) => Encoding.UTF8.GetBytes("""{"msg":{"cmd":"turn","data":{"value":""" + (on ? "1" : "0") + "}}}");

    public static byte[] Brightness(int percent) =>
        Encoding.UTF8.GetBytes("""{"msg":{"cmd":"brightness","data":{"value":""" + Math.Clamp(percent, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}");

    private static ReadOnlySpan<byte> ColorStart => """{"msg":{"cmd":"colorwc","data":{"color":{"r":"""u8;

    private static ReadOnlySpan<byte> ColorG => ""","g":"""u8;

    private static ReadOnlySpan<byte> ColorB => ""","b":"""u8;

    private static ReadOnlySpan<byte> ColorEnd => """},"colorTemInKelvin":0}}}"""u8;

    /// <summary>Writes a <c>colorwc</c> command into <paramref name="packet"/> without allocating; returns its length.</summary>
    public static int WriteColor(Span<byte> packet, byte r, byte g, byte b)
    {
        var n = 0;
        Append(packet, ref n, ColorStart);
        Utf8Formatter.TryFormat(r, packet[n..], out var w);
        n += w;
        Append(packet, ref n, ColorG);
        Utf8Formatter.TryFormat(g, packet[n..], out w);
        n += w;
        Append(packet, ref n, ColorB);
        Utf8Formatter.TryFormat(b, packet[n..], out w);
        n += w;
        Append(packet, ref n, ColorEnd);
        return n;
    }

    private static void Append(Span<byte> packet, ref int n, ReadOnlySpan<byte> text)
    {
        text.CopyTo(packet[n..]);
        n += text.Length;
    }
}

/// <summary>
/// Govee Wi-Fi lights with LAN Control turned on (in the Govee app): one color per light over UDP (<c>colorwc</c>),
/// switched on with its brightness when the device opens and off when it closes. Govee lights drop commands that come
/// too fast, so a light gets a new color at most every <see cref="GoveeOptions.UpdateInterval"/> (100 ms by default)
/// and only when it changed (every 2 s anyway, in case a datagram was lost); the runner resends at that interval, so
/// the last color always arrives. Allocation-free per frame. Not in the C++ code.
/// </summary>
public sealed class GoveeDriver : ILedDriver
{
    public const string TypeName = "govee";

    public static readonly TimeSpan DefaultUpdateInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The time between commands that aren't colors (on, brightness): Govee lights drop commands that come too fast.</summary>
    public static readonly TimeSpan DefaultCommandSpacing = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan s_resendAll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_scanTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_blinkStep = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan s_statusTime = TimeSpan.FromSeconds(1);

    private readonly GoveeOptions _options;
    private readonly IDatagramChannel[] _channels;
    private readonly TimeProvider _time;
    private readonly byte[] _packet = new byte[GoveeProtocol.MaxColorLength];
    private readonly int[] _sent;
    private bool _connected;
    private long? _lastSend;
    private long? _lastFull;

    public GoveeDriver(GoveeOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (options.Lamps.Count == 0)
        {
            throw new ArgumentException("No Govee light with a valid IP address is configured.", nameof(options));
        }

        _options = options;
        _time = context.Time;
        _channels = [.. options.Lamps.Select(_ => context.CreateDatagramChannel())];
        _sent = new int[options.Lamps.Count];
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new GoveeDriver(GoveeOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
        Identify = IdentifyAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy => RetryPolicy.Default;

    public string? Status => null;

    /// <summary>
    /// Sends Govee's scan to 239.255.255.250:4001 from port 4002 of each interface address and lists every light that
    /// answers within 2 s (one entry per device id / address): <c>{ name, ipAddress, deviceId, sku, value }</c>.
    /// </summary>
    public static async Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var replies = await request.Context.Discovery.Multicast.ProbeAsync(
            GoveeProtocol.Scan, new IPEndPoint(GoveeProtocol.ScanGroup, GoveeProtocol.ScanPort), GoveeProtocol.ReplyPort, s_scanTime, request.CancellationToken);
        return ParseScanReplies(replies.Select(r => (r.From.Address, r.Data)));
    }

    /// <summary>The scan answers as discovery results (internal for the tests).</summary>
    internal static JsonArray ParseScanReplies(IEnumerable<(IPAddress From, byte[] Data)> replies)
    {
        var lights = new JsonArray();
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, data) in replies)
        {
            JsonObject? reply;
            try
            {
                reply = (JsonNode.Parse(data) as JsonObject)?["msg"] as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (reply is null || DeviceConfig.GetString(reply, "cmd") != "scan" || reply["data"] is not JsonObject d)
            {
                continue;
            }

            var ip = IPAddress.TryParse(DeviceConfig.GetString(d, "ip"), out var address) ? address.ToString() : from.ToString();
            var id = DeviceConfig.GetString(d, "device");
            var sku = DeviceConfig.GetString(d, "sku");
            // one entry per address, and per device id when the light reports one
            if (!seenAddresses.Add(ip) || (id.Length > 0 && !seenIds.Add(id)))
            {
                continue;
            }

            lights.Add((JsonNode)new JsonObject
            {
                ["name"] = sku.Length > 0 ? $"Govee {sku} ({ip})" : $"Govee light #{lights.Count}",
                ["ipAddress"] = ip,
                ["deviceId"] = id,
                ["sku"] = sku,
                ["value"] = ip,
            });
        }

        return lights;
    }

    /// <summary>
    /// <c>{ ipAddress }</c>: a short blink (on at full brightness in white, off, on, off), then the light as it was: its
    /// state is asked first (<c>devStatus</c>, answered at port 4002 like the scan); a light that doesn't answer stays
    /// off. The commands are spaced (<see cref="DefaultCommandSpacing"/>), as Govee lights drop those that come faster.
    /// </summary>
    public static async Task IdentifyAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IPAddress.TryParse(DeviceConfig.GetString(request.Parameters, "ipAddress"), out var address))
        {
            return;
        }

        var time = request.Context.Time;
        var token = request.CancellationToken;
        var replies = await request.Context.Discovery.Multicast.ProbeAsync(
            GoveeProtocol.DevStatus, new IPEndPoint(address, GoveeProtocol.ControlPort), GoveeProtocol.ReplyPort, s_statusTime, token);
        var status = replies.Where(r => r.From.Address.Equals(address)).Select(r => GoveeProtocol.ReadStatus(r.Data)).FirstOrDefault(s => s is not null);

        using var channel = request.Context.CreateDatagramChannel();
        channel.Connect(new IPEndPoint(address, GoveeProtocol.ControlPort));
        var packet = new byte[GoveeProtocol.MaxColorLength];
        async Task SendAsync(ReadOnlyMemory<byte> message, TimeSpan after)
        {
            channel.Send(message.Span);
            await Task.Delay(after, time, token);
        }

        await SendAsync(GoveeProtocol.Turn(true), DefaultCommandSpacing);
        await SendAsync(GoveeProtocol.Brightness(100), DefaultCommandSpacing);
        await SendAsync(packet.AsMemory(0, GoveeProtocol.WriteColor(packet, 255, 255, 255)), s_blinkStep);
        await SendAsync(GoveeProtocol.Turn(false), s_blinkStep);
        await SendAsync(GoveeProtocol.Turn(true), s_blinkStep);
        await SendAsync(GoveeProtocol.Turn(false), DefaultCommandSpacing);
        if (status is not { On: true } was)
        {
            return;
        }

        await SendAsync(GoveeProtocol.Turn(true), DefaultCommandSpacing);
        await SendAsync(GoveeProtocol.Brightness(was.Brightness), DefaultCommandSpacing);
        if (was.Color is { } c && (c.R | c.G | c.B) != 0)
        {
            channel.Send(packet.AsSpan(0, GoveeProtocol.WriteColor(packet, c.R, c.G, c.B)));
        }
        else if (was.Kelvin > 0)
        {
            channel.Send(GoveeProtocol.ColorTemperature(was.Kelvin));
        }
    }
    public TimeSpan? EffectiveRefreshInterval(TimeSpan? configured) => _options.Interval;

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _channels.Length; i++)
        {
            _channels[i].Connect(new IPEndPoint(_options.Lamps[i].Address, GoveeProtocol.ControlPort));
        }

        _connected = true;
        Array.Fill(_sent, -1);
        _lastSend = null;
        _lastFull = null;
        SendAll(GoveeProtocol.Turn(true));
        await Task.Delay(_options.Spacing, _time, cancellationToken);
        SendAll(GoveeProtocol.Brightness(_options.Brightness));
        await Task.Delay(_options.Spacing, _time, cancellationToken);
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        if (!_connected)
        {
            return false;
        }

        var now = _time.GetTimestamp();
        if (_lastSend is { } last && _time.GetElapsedTime(last, now) < _options.Interval)
        {
            // too soon for the lights: the runner's resend brings the newest color
            return true;
        }

        var all = _lastFull is not { } full || _time.GetElapsedTime(full, now) >= s_resendAll;
        var ok = true;
        var n = Math.Min(colors.Length / 3, _channels.Length);
        for (var i = 0; i < n; i++)
        {
            var r = colors[i * 3];
            var g = colors[(i * 3) + 1];
            var b = colors[(i * 3) + 2];
            var rgb = (r << 16) | (g << 8) | b;
            if (!all && _sent[i] == rgb)
            {
                continue;
            }

            ok &= _channels[i].Send(_packet.AsSpan(0, GoveeProtocol.WriteColor(_packet, r, g, b)));
            _sent[i] = rgb;
        }

        _lastSend = now;
        _lastFull = all ? now : _lastFull;
        return ok;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            SendAll(GoveeProtocol.Turn(false));
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

    private void SendAll(byte[] message)
    {
        foreach (var channel in _channels)
        {
            channel.Send(message);
        }
    }
}
