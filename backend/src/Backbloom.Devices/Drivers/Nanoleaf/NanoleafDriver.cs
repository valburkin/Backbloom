using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Devices.Transport;

namespace Backbloom.Devices.Drivers.Nanoleaf;

/// <summary>Settings of the "nanoleaf" driver (sources/led-drivers/schemas/schema-nanoleaf.json).</summary>
/// <param name="TopDown">Panel order top to bottom (<c>panelOrderTopDown</c> 0).</param>
/// <param name="LeftRight">Panel order left to right (<c>panelOrderLeftRight</c> 0).</param>
public sealed record NanoleafOptions(string Host, string Token, bool TopDown = true, bool LeftRight = true, int StartPosition = 0)
{
    public static NanoleafOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new NanoleafOptions(
            DeviceConfig.GetString(config, "host"),
            DeviceConfig.GetString(config, "token"),
            DeviceConfig.GetIntOrString(config, "panelOrderTopDown", 0) == 0,
            DeviceConfig.GetIntOrString(config, "panelOrderLeftRight", 0) == 0,
            DeviceConfig.GetInt(config, "panelStartPos", 0));
    }
}

/// <summary>
/// Nanoleaf Light Panels / Canvas / Shapes: reads the panel layout over the OpenAPI (port 16021), switches to External
/// Control v2 and streams UDP frames (port of <c>DriverNetNanoleaf</c>, sources/led-drivers/net/DriverNetNanoleaf.cpp).
/// </summary>
public sealed partial class NanoleafDriver : ILedDriver
{
    public const string TypeName = "nanoleaf";
    public const int ApiPort = 16021;
    public const int StreamPort = 60222;
    public const string ExternalControlV2 = """{"write" : {"command" : "display", "animType" : "extControl", "extControlVersion" : "v2"}}""";

    private const int RhythmShape = 1;

    private readonly NanoleafOptions _options;
    private readonly JsonRest _rest;
    private readonly IDatagramChannel _udp;
    private readonly Func<string, CancellationToken, ValueTask<IPAddress>> _resolve;
    private readonly ILogger _logger;
    private readonly string _host;
    private readonly int _apiPort;
    private ushort[] _panelIds = [];
    private byte[] _packet = [];
    private int _endPosition;
    private bool _connected;

    public NanoleafDriver(NanoleafOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("No Nanoleaf host name or IP address is configured.", nameof(options));
        }

        _options = options;
        (_host, var port) = DeviceConfig.SplitHostPort(options.Host);
        _apiPort = port ?? ApiPort;
        _rest = new JsonRest(context.Http);
        _udp = context.CreateDatagramChannel();
        _resolve = context.ResolveHost;
        _logger = context.LoggerFactory.CreateLogger<NanoleafDriver>();
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new NanoleafDriver(NanoleafOptions.FromConfig(config), context))
    {
        Discover = request => SsdpDevices.SearchAsync(request, "ssdp:all", 1900, "ST", "nanoleaf:nl29|nanoleaf_aurora:light", false, (ip, _) => $"Nanoleaf {ip}"),
        GetProperties = GetPropertiesAsync,
        Identify = IdentifyAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy => RetryPolicy.Default;

    public string? Status { get; private set; }

    /// <summary>Panel ids in streaming order (internal for the layout tests).</summary>
    internal IReadOnlyList<ushort> PanelIds => _panelIds;

    /// <summary>
    /// Orders the panels like the C++ code: rows from the highest y down, within a row by x (left to right or right
    /// to left); bottom-up order reverses the whole list. Rhythm modules are skipped.
    /// </summary>
    public static ushort[] OrderPanels(JsonArray positionData, bool topDown, bool leftRight)
    {
        ArgumentNullException.ThrowIfNull(positionData);
        var rows = new SortedDictionary<int, SortedDictionary<int, ushort>>();
        foreach (var panel in positionData.OfType<JsonObject>())
        {
            if (DeviceConfig.GetInt(panel, "shapeType", 0) == RhythmShape)
            {
                continue;
            }

            var y = DeviceConfig.GetInt(panel, "y", 0);
            if (!rows.TryGetValue(y, out var row))
            {
                row = [];
                rows[y] = row;
            }

            row[DeviceConfig.GetInt(panel, "x", 0)] = (ushort)DeviceConfig.GetInt(panel, "panelId", 0);
        }

        var ordered = new LinkedList<ushort>();
        foreach (var row in rows.Reverse())
        {
            foreach (var id in leftRight ? row.Value.Values : row.Value.Values.Reverse())
            {
                if (topDown)
                {
                    ordered.AddLast(id);
                }
                else
                {
                    ordered.AddFirst(id);
                }
            }
        }

        return [.. ordered];
    }

    /// <summary>External Control v2 frame: panel count, then per panel id, R, G, B, W (0), transition time 1 (100 ms).</summary>
    public static void WriteFrame(Span<byte> packet, ReadOnlySpan<ushort> panelIds, ReadOnlySpan<byte> rgb, int startPosition, int endPosition)
    {
        BinaryPrimitives.WriteUInt16BigEndian(packet, (ushort)panelIds.Length);
        var led = 0;
        for (var p = 0; p < panelIds.Length; p++)
        {
            var o = 2 + (p * 8);
            BinaryPrimitives.WriteUInt16BigEndian(packet[o..], panelIds[p]);
            if (p >= startPosition && p <= endPosition && (led * 3) + 2 < rgb.Length)
            {
                packet[o + 2] = rgb[led * 3];
                packet[o + 3] = rgb[(led * 3) + 1];
                packet[o + 4] = rgb[(led * 3) + 2];
                led++;
            }
            else
            {
                packet[o + 2] = packet[o + 3] = packet[o + 4] = 0;
            }

            packet[o + 5] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(packet[(o + 6)..], 1);
        }
    }

    public static async Task<JsonObject> GetPropertiesAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = request.Parameters;
        var host = DeviceConfig.GetString(p, "host");
        if (host.Length == 0)
        {
            return [];
        }

        var reply = await new JsonRest(request.Context.Http).GetAsync(ApiUri(host, DeviceConfig.GetString(p, "token"), DeviceConfig.GetString(p, "filter")), request.CancellationToken);
        return reply.Body as JsonObject ?? [];
    }

    public static async Task IdentifyAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = request.Parameters;
        var host = DeviceConfig.GetString(p, "host");
        if (host.Length > 0)
        {
            await new JsonRest(request.Context.Http).PutAsync(ApiUri(host, DeviceConfig.GetString(p, "token"), "identify"), string.Empty, request.CancellationToken);
        }
    }

    public TimeSpan? EffectiveRefreshInterval(TimeSpan? configured) => null;

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        var info = await _rest.GetAsync(Api(string.Empty), cancellationToken);
        if (!info.IsSuccess || info.Body is not JsonObject device)
        {
            throw new IOException($"Nanoleaf {_options.Host}: couldn't read the panel layout (HTTP {info.Status}).");
        }

        var layout = device["panelLayout"]?["layout"] as JsonObject;
        _panelIds = OrderPanels(layout?["positionData"] as JsonArray ?? [], _options.TopDown, _options.LeftRight);
        _endPosition = _options.StartPosition + ledCount - 1;
        var model = DeviceConfig.GetString(device, "model");
        var firmware = DeviceConfig.GetString(device, "firmwareVersion");
        LogPanels(_options.Host, model, firmware, _panelIds.Length);
        if (_panelIds.Length < ledCount)
        {
            throw new InvalidOperationException($"Not enough panels [{_panelIds.Length}] for configured LEDs [{ledCount}] found!");
        }

        if (_endPosition >= _panelIds.Length)
        {
            throw new InvalidOperationException($"Start panel [{_options.StartPosition}] out of range. Start panel position can be max [{_panelIds.Length - ledCount}] given [{_panelIds.Length}] panel available!");
        }

        var streamPort = await ExternalControlAsync(cancellationToken);
        var address = await _resolve(_host, cancellationToken);
        _udp.Connect(new IPEndPoint(address, streamPort));
        _packet = new byte[2 + (_panelIds.Length * 8)];

        // switchOn -> powerOn: external control once more, then the panels on
        await ExternalControlAsync(cancellationToken);
        await _rest.PutAsync(Api("state"), OnOffRequest(true), cancellationToken);
        _connected = true;
        Status = $"{_panelIds.Length} panels";
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        if (!_connected)
        {
            return false;
        }

        WriteFrame(_packet, _panelIds, colors, _options.StartPosition, _endPosition);
        return _udp.Send(_packet);
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            _connected = false;
            await _rest.PutAsync(Api("state"), OnOffRequest(false), cancellationToken);
        }

        Status = null;
    }

    public void Dispose() => _udp.Dispose();

    public static string OnOffRequest(bool on) => on ? """{"on":{"value":true}}""" : """{"on":{"value":false}}""";

    private static Uri ApiUri(string host, string token, string path)
    {
        var (h, port) = DeviceConfig.SplitHostPort(host);
        return new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{DeviceConfig.UrlHost(h)}:{port ?? ApiPort}/api/v1/{token}/{path}"));
    }

    private Uri Api(string path) => new(string.Create(CultureInfo.InvariantCulture, $"http://{DeviceConfig.UrlHost(_host)}:{_apiPort}/api/v1/{_options.Token}/{path}"));

    /// <summary>Enables External Control v2; Light Panels answer with their stream port, Canvas uses 60222.</summary>
    private async Task<int> ExternalControlAsync(CancellationToken cancellationToken)
    {
        var reply = await _rest.PutAsync(Api("effects"), ExternalControlV2, cancellationToken);
        return reply.Body is JsonObject o && DeviceConfig.GetInt(o, "streamControlPort", 0) is > 0 and <= 65535 and var port ? port : StreamPort;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Nanoleaf {Host}: model {Model}, firmware {Firmware}, {Panels} panels")]
    private partial void LogPanels(string host, string model, string firmware, int panels);
}
