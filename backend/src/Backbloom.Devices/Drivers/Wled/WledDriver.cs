using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Devices.Transport;

namespace Backbloom.Devices.Drivers.Wled;

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
/// (sources/led-drivers/net/DriverNetWled.cpp). Not in the C++ code: on connecting it reads the power limiter settings
/// (<c>GET /json/cfg</c>), and while connected <c>GET /json/si</c> every <see cref="TelemetryInterval"/> for the
/// <see cref="Telemetry"/>: WLED's power estimate and limit, brightness, frame rate, realtime state, Wi-Fi, free memory,
/// uptime (a restart is logged and the realtime state set again) and a temperature usermod's reading.
/// </summary>
public sealed partial class WledDriver : ITelemetryDriver
{
    public const string TypeName = "wled";

    public static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(1500);

    // the boot time computed from the uptime moves by up to a few seconds between readings (whole seconds, delays)
    private static readonly TimeSpan s_bootTolerance = TimeSpan.FromSeconds(30);

    // failed status reads in a row before the log says so (one alone is common on Wi-Fi)
    private const int FailuresToReport = 3;

    // The power settings are read again every this many readings (about once a minute), as they now steer the output.
    private const int ReadingsPerConfigRead = 6;

    private readonly WledOptions _options;
    private readonly HttpClient _http;
    private readonly IDatagramChannel _udp;
    private readonly Func<string, CancellationToken, ValueTask<IPAddress>> _resolve;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly byte[] _packet = new byte[WledProtocol.MaxPacketSize];
    private readonly Lock _telemetryLock = new();
    private string? _savedState;
    private volatile bool _connected;
    private CancellationTokenSource? _polling;
    private Task? _poll;
    private volatile ControllerTelemetry? _telemetry;
    private WledConfig _config;
    private string? _version;

    // under _telemetryLock
    private DateTimeOffset? _bootTime;
    private int _restarts;
    private int? _polledBrightness;

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
        _time = context.Time;
        RetryPolicy = new RetryPolicy(Math.Max(0, options.MaxRetry), s_retryDelay, RetryPolicy.KeepTrying);
    }

    public string Type => TypeName;

    public RetryPolicy RetryPolicy { get; }

    public string? Status { get; private set; }

    public ControllerTelemetry? Telemetry => _telemetry;

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new WledDriver(WledOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
    };

    /// <summary>WLED controllers announced with mDNS (<c>_wled._tcp</c>): <c>value</c> is the IP address.</summary>
    public static async Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var list = new JsonArray();
        foreach (var s in await request.Context.Discovery.Mdns.BrowseAsync(Backbloom.Discovery.KnownServices.Wled, TimeSpan.FromSeconds(2), request.CancellationToken))
        {
            var ip = s.Address.ToString();
            list.Add((JsonNode)new JsonObject { ["value"] = ip, ["name"] = $"{ip} ({s.HostName})" });
        }

        return list;
    }

    // The JSON API at the address the host had when connecting (set by OpenAsync before the first request).
    private Uri BaseUri { get; set; } = null!;

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        // a reconnect: the previous connection's readings stop first, so they can't mix with this one's power settings
        await StopPollingAsync();

        // the name is resolved once per connection (by the shared resolver, which falls back to mDNS and the last
        // address) and the API and the stream both use the address, so HttpClient never resolves it itself
        var address = await _resolve(_options.Host, cancellationToken);
        BaseUri = new Uri($"http://{FormatHost(address)}:{_options.ApiPort}/json");
        var info = await GetJsonAsync(BaseUri, cancellationToken);
        var udpPort = WledProtocol.DefaultUdpPort;
        ControllerTelemetry? telemetry = null;
        if (info is not null)
        {
            udpPort = ReadInfo(info, ledCount);
            _version = ReadString((info["info"] as JsonObject)?["ver"]);
            await ReadConfigAsync(keepOnFailure: false, cancellationToken);
            _polledBrightness = null;
            (telemetry, _) = ReadTelemetry(info, polled: false);
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

        _udp.Connect(new IPEndPoint(address, udpPort));
        _connected = true;

        // published once connected: a failed open leaves no readings behind; the brightness is the one just set
        _telemetry = _options.OverrideBrightness && telemetry is not null ? telemetry with { Brightness = _options.BrightnessLevel } : telemetry;
        Status = telemetry?.SignalPercent is { } signal ? $"{signal}%" : Status;
        await StopPollingAsync();
        _polling = new CancellationTokenSource();
        _poll = PollAsync(_polling);
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

        // the poll ends first, so its requests (a realtime state sent again) can't come after the switch-off
        await StopPollingAsync();

        // no readings while switched off (the restart count and boot time stay for the next connection)
        _telemetry = null;
        Status = null;
        await PutStateAsync(WledProtocol.OnOffRequest(false, _options.OverrideBrightness, _options.BrightnessLevel, _savedState), cancellationToken);
    }

    public void Dispose()
    {
        _polling?.Cancel();
        _udp.Dispose();
    }

    // Cancels the poll and waits for it to end (it owns its token source and disposes it).
    private async Task StopPollingAsync()
    {
        _polling?.Cancel();
        _polling = null;
        if (_poll is { } poll)
        {
            _poll = null;
            await poll.ConfigureAwait(false);
        }
    }

    // Reads state and info (/json/si: without the effect and palette lists) every TelemetryInterval.
    private async Task PollAsync(CancellationTokenSource polling)
    {
        var token = polling.Token;
        var failures = 0;
        var readings = 0;
        var resendPending = false;
        try
        {
            while (true)
            {
                await Task.Delay(TelemetryInterval, _time, token);
                try
                {
                    if (await GetJsonAsync(new Uri(BaseUri, "json/si"), token) is not { } json)
                    {
                        continue;
                    }

                    // the power settings steer the output (PowerGroups): changes made in the WLED app count soon
                    if (++readings % ReadingsPerConfigRead == 0)
                    {
                        await ReadConfigAsync(keepOnFailure: true, token);
                    }

                    var (telemetry, restarted) = ReadTelemetry(json, polled: true);
                    token.ThrowIfCancellationRequested();
                    _telemetry = telemetry;
                    resendPending |= restarted;
                    if (resendPending)
                    {
                        // a restart loses the realtime state Backbloom set (on, brightness): set it again, until that
                        // succeeds (a controller that just booted may not answer yet); the brightness it sets is no
                        // change made elsewhere; its settings may have changed too
                        await ReadConfigAsync(keepOnFailure: true, token);
                        await PutStateAsync(WledProtocol.OnOffRequest(true, _options.OverrideBrightness, _options.BrightnessLevel, _savedState), token);
                        resendPending = false;
                        lock (_telemetryLock)
                        {
                            _polledBrightness = null;
                        }
                    }

                    failures = 0;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && ex is not OutOfMemoryException)
                {
                    // the stream itself reports a lost controller; this only says the readings are getting old
                    if (++failures == FailuresToReport)
                    {
                        LogTelemetryFailed(_options.Host, failures, ex.Message);
                    }
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // closed: whatever the last request ended with, the switch-off goes ahead
        }
        finally
        {
            polling.Dispose();
        }
    }

    // The power limiter settings and LED current from /json/cfg, on connecting and every ReadingsPerConfigRead readings
    // (/json/si can't tell whether the limiter is in use). A failure on connecting leaves them unknown; a later one keeps
    // the last ones.
    private async Task ReadConfigAsync(bool keepOnFailure, CancellationToken cancellationToken)
    {
        WledConfig? config = null;
        try
        {
            if (await GetJsonAsync(new Uri(BaseUri, "json/cfg"), cancellationToken) is { } cfg)
            {
                config = WledConfig.Read(cfg, _version);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            if (!keepOnFailure)
            {
                LogNoPowerSettings(_options.Host, ex.Message);
            }
        }

        _config = config ?? (keepOnFailure ? _config : default);
    }

    // The readings, and whether the controller restarted since the previous one (while connected: it then lost the
    // realtime state; noticed on connecting, the restart was while switched off, e.g. its power supply was off).
    private (ControllerTelemetry Telemetry, bool Restarted) ReadTelemetry(JsonObject json, bool polled)
    {
        var state = json["state"] as JsonObject;
        var info = json["info"] as JsonObject;
        var leds = info?["leds"] as JsonObject;
        var wifi = info?["wifi"] as JsonObject;
        var config = _config;
        var uptime = ReadLong(info?["uptime"]);
        var temperature = WledTemperature.Read(info);
        var signal = ReadInt(wifi?["signal"]);
        var brightness = ReadInt(state?["bri"]);
        var now = _time.GetUtcNow();
        bool restarted;
        ControllerTelemetry telemetry;
        lock (_telemetryLock)
        {
            // the uptime alone misses a restart while switched off (a later uptime can still be larger)
            var boot = uptime is { } up ? now - TimeSpan.FromSeconds(up) : (DateTimeOffset?)null;
            restarted = boot > _bootTime + s_bootTolerance;
            if (restarted && polled)
            {
                _restarts++;
                LogRestarted(_options.Host, uptime!.Value);
            }
            else if (restarted)
            {
                LogRestartedWhileOff(_options.Host, uptime!.Value);
            }

            _bootTime = boot ?? _bootTime;
            if (polled && brightness is { } b && _polledBrightness is { } before && b != before)
            {
                LogBrightnessChanged(_options.Host, before, b);
            }

            _polledBrightness = polled ? brightness : _polledBrightness;
            telemetry = new ControllerTelemetry(now)
            {
                // WLED's estimate (the LEDs and its own 80-120 mA) is only real while its limiter is in use
                PowerMilliamps = config.LimiterInUse && ReadInt(leds?["pwr"]) is { } power and > 0 ? power : null,
                PowerLimitMilliamps = config.LimiterInUse ? config.PowerLimitMilliamps : null,
                MilliampsPerLed = config.MilliampsPerLed,
                PowerGroups = config.LimiterInUse ? config.Groups : null,
                Brightness = brightness,
                ForceMaxBrightness = config.ForceMaxBrightness,
                On = ReadBool(state?["on"]),
                FramesPerSecond = ReadInt(leds?["fps"]),
                LedCount = ReadInt(leds?["count"]),
                Live = ReadBool(info?["live"]),
                LiveMode = ReadString(info?["lm"]) is { Length: > 0 } mode ? mode : null,
                LiveSource = ReadString(info?["lip"]) is { Length: > 0 } source ? source : null,
                RealtimeOverride = ReadInt(state?["lor"]),
                Clients = ReadInt(info?["ws"]),
                Chip = ReadString(info?["arch"]),
                SignalPercent = signal,
                Rssi = ReadInt(wifi?["rssi"]),
                WifiChannel = ReadInt(wifi?["channel"]),
                FreeMemoryBytes = ReadLong(info?["freeheap"]),
                UptimeSeconds = uptime,
                Restarts = _restarts,
                TemperatureCelsius = temperature?.Celsius,
                TemperatureSource = temperature?.Sensor,
                TemperatureIsChip = temperature?.IsChip ?? false,
                Firmware = ReadString(info?["ver"]),
            };
        }

        if (polled && _connected && signal is { } s)
        {
            Status = $"{s}%";
        }

        return (telemetry, restarted);
    }

    private static bool? ReadBool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    private static string? ReadString(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    // an IPv6 scope stays as "%12": .NET doesn't decode RFC 6874's "%25", and Uri.ToString() drops the scope, so the
    // request URIs are made relative to BaseUri rather than from its string
    private static string FormatHost(IPAddress address) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"[{address}]"
        : address.ToString();

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

    private static long? ReadLong(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? (long)d : null;

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
        using var response = await _http.PutAsync(new Uri(BaseUri, "json/state"), content, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED {Host}: Wi-Fi {Signal}% (channel {Channel}), {Leds} LEDs, version {Version}, UDP port {Port}")]
    private partial void LogControllerInfo(string host, int signal, int channel, int leds, string version, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WLED {Host}: weak Wi-Fi signal ({Signal}%)")]
    private partial void LogWeakSignal(string host, int signal);

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED {Host}: its power limiter is set to {Milliamps} mA: it dims the LEDs when they would draw more (colors may look duller then)")]
    private partial void LogPowerLimiter(string host, int milliamps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WLED {Host}: the controller restarted (up for {Seconds} s): check its power supply and temperature")]
    private partial void LogRestarted(string host, long seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WLED {Host}: couldn't read the controller's status {Failures} times in a row: {Reason}")]
    private partial void LogTelemetryFailed(string host, int failures, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED {Host}: the controller restarted while switched off (up for {Seconds} s)")]
    private partial void LogRestartedWhileOff(string host, long seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "WLED {Host}: its brightness changed from {Before} to {After} (in the WLED app or by another program)")]
    private partial void LogBrightnessChanged(string host, int before, int after);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WLED {Host}: couldn't read its power settings (/json/cfg): {Reason}")]
    private partial void LogNoPowerSettings(string host, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WLED {Host}: Backbloom drives {Configured} LEDs but the controller has {Controller}")]
    private partial void LogLedCountMismatch(string host, int configured, int controller);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WLED {Host}: couldn't read the controller info, using the default UDP port")]
    private partial void LogNoConfig(string host);
}
