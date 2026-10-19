using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Devices.Transport;

namespace Overglow.Devices.Drivers.Wled;

/// <summary>Settings of the "wled" driver (sources/led-drivers/schemas/schema-wled.json).</summary>
public sealed record WledOptions(
    string Host,
    int ApiPort = 80,
    bool OverrideBrightness = true,
    int BrightnessLevel = 255,
    bool RestoreOriginalState = false,
    int MaxRetry = 60)
{
    public static WledOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var (host, port) = DeviceConfig.SplitHostPort(DeviceConfig.GetString(config, "host"));
        return new WledOptions(
            host,
            port ?? 80,
            DeviceConfig.GetBool(config, "brightnessMax", true),
            DeviceConfig.GetInt(config, "brightnessMaxLevel", 255),
            DeviceConfig.GetBool(config, "restoreOriginalState", false),
            DeviceConfig.GetInt(config, "maxRetry", 60));
    }
}

/// <summary>
/// WLED controller: reads <c>GET /json</c> (LED count, UDP port, Wi-Fi signal, current state), switches realtime mode
/// on with <c>PUT /json/state</c> and streams frames over UDP. Port of <c>DriverNetWled</c>
/// (sources/led-drivers/net/DriverNetWled.cpp).
/// </summary>
public sealed partial class WledDriver : ILedDriver
{
    public const string TypeName = "wled";

    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(1500);

    private readonly WledOptions _options;
    private readonly HttpClient _http;
    private readonly IDatagramChannel _udp;
    private readonly Func<string, CancellationToken, ValueTask<IPAddress>> _resolve;
    private readonly ILogger _logger;
    private readonly byte[] _packet = new byte[WledProtocol.MaxPacketSize];
    private string? _savedState;
    private bool _connected;

    public WledDriver(WledOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("No WLED host name or IP address is configured.", nameof(options));
        }

        _options = options;
        _http = context.Http;
        _udp = context.CreateDatagramChannel();
        _resolve = context.ResolveHost;
        _logger = context.LoggerFactory.CreateLogger<WledDriver>();
        RetryPolicy = new RetryPolicy(Math.Max(0, options.MaxRetry), s_retryDelay);
    }

    public string Type => TypeName;

    public RetryPolicy RetryPolicy { get; }

    public string? Status { get; private set; }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new WledDriver(WledOptions.FromConfig(config), context));

    private Uri BaseUri => new($"http://{FormatHost(_options.Host)}:{_options.ApiPort}/json");

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        var info = await GetJsonAsync(BaseUri, cancellationToken);
        var udpPort = WledProtocol.DefaultUdpPort;
        if (info is not null)
        {
            udpPort = ReadInfo(info, ledCount);
            if (_options.RestoreOriginalState && info["state"] is JsonObject state)
            {
                var saved = state.DeepClone().AsObject();
                saved["live"] = false;
                _savedState = saved.ToJsonString();
            }
        }
        else
        {
            LogNoConfig(_options.Host);
        }

        await PutStateAsync(WledProtocol.OnOffRequest(true, _options.OverrideBrightness, _options.BrightnessLevel, _savedState), cancellationToken);

        var address = await _resolve(_options.Host, cancellationToken);
        _udp.Connect(new IPEndPoint(address, udpPort));
        _connected = true;
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        if (!_connected)
        {
            return false;
        }

        var ok = true;
        var packets = WledProtocol.PacketCount(colors.Length / 3);
        for (var i = 0; i < packets; i++)
        {
            var length = WledProtocol.WriteFrame(colors, i, _packet);
            ok &= _udp.Send(_packet.AsSpan(0, length));
        }

        return ok;
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        _connected = false;
        Status = null;
        await PutStateAsync(WledProtocol.OnOffRequest(false, _options.OverrideBrightness, _options.BrightnessLevel, _savedState), cancellationToken);
    }

    public void Dispose() => _udp.Dispose();

    private static string FormatHost(string host) => IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host;

    private int ReadInfo(JsonObject json, int ledCount)
    {
        var info = json["info"] as JsonObject;
        var leds = info?["leds"] as JsonObject;
        var wifi = info?["wifi"] as JsonObject;
        var wledLeds = ReadInt(leds?["count"]) ?? 0;
        var powerLimit = ReadInt(leds?["maxpwr"]) ?? 0;
        var signal = ReadInt(wifi?["signal"]) ?? 0;
        var udpPort = ReadInt(info?["udpport"]) is { } p and > 0 and <= 65535 ? p : WledProtocol.DefaultUdpPort;
        Status = $"{signal}%";

        var channel = ReadInt(wifi?["channel"]) ?? 0;
        var version = info?["ver"] is JsonValue v && v.TryGetValue(out string? ver) ? ver : "?";
        LogControllerInfo(_options.Host, signal, channel, wledLeds, version, udpPort);
        if (signal < 80)
        {
            LogWeakSignal(_options.Host, signal);
        }

        if (powerLimit > 0)
        {
            LogPowerLimiter(_options.Host, powerLimit);
        }

        if (wledLeds != ledCount)
        {
            LogLedCountMismatch(_options.Host, ledCount, wledLeds);
        }

        return udpPort;
    }

    private static int? ReadInt(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? (int)d : null;

    private async Task<JsonObject?> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_requestTimeout);
        using var response = await _http.GetAsync(uri, timeout.Token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject;
    }

    private async Task PutStateAsync(string body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_requestTimeout);
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PutAsync(new Uri(BaseUri + "/state"), content, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED {Host}: Wi-Fi {Signal}% (channel {Channel}), {Leds} LEDs, version {Version}, UDP port {Port}")]
    private partial void LogControllerInfo(string host, int signal, int channel, int leds, string version, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WLED {Host}: weak Wi-Fi signal ({Signal}%)")]
    private partial void LogWeakSignal(string host, int signal);

    [LoggerMessage(Level = LogLevel.Error, Message = "WLED {Host}: the WLED power limiter is set ({Milliamps} mA); this may dim or distort colors. Use a power supply that fits the strip instead.")]
    private partial void LogPowerLimiter(string host, int milliamps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WLED {Host}: Overglow drives {Configured} LEDs but the controller has {Controller}")]
    private partial void LogLedCountMismatch(string host, int configured, int controller);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WLED {Host}: couldn't read the controller info, using the default UDP port")]
    private partial void LogNoConfig(string host);
}
