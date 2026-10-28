using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Backbloom.Devices.Drivers.Yeelight;

/// <summary>Settings of the "yeelight" driver (sources/led-drivers/schemas/schema-yeelight.json).</summary>
/// <param name="Rgb">Color model: RGB (<c>colorModel</c> 1) or HSV (0).</param>
/// <param name="Smooth">Transition effect: smooth (<c>transEffect</c> 0) or sudden (1).</param>
/// <param name="ResponseSilence">How long a command waits for more answers after the first one (500 ms in C++).</param>
public sealed record YeelightOptions(
    IReadOnlyList<(string Host, int Port, string Name)> Lights,
    bool Rgb = true,
    bool Smooth = true,
    int TransitionMs = 50,
    int ExtraTimeDarknessMs = 0,
    int BrightnessMin = 0,
    bool SwitchOffOnMinimum = true,
    int BrightnessMax = 100,
    double BrightnessFactor = 1.0,
    bool RestoreOriginalState = false,
    int MaxRetry = 60,
    TimeSpan QuotaWait = default,
    TimeSpan ResponseSilence = default)
{
    public const int DefaultPort = 55443;

    public static YeelightOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var lights = new List<(string, int, string)>();
        foreach (var light in DeviceConfig.GetObjects(config, "lights"))
        {
            var host = DeviceConfig.GetString(light, "host");
            if (host.Length > 0)
            {
                lights.Add((host.Split(':', StringSplitOptions.RemoveEmptyEntries)[0], DeviceConfig.GetInt(light, "port", DefaultPort), DeviceConfig.GetString(light, "name")));
            }
        }

        return new YeelightOptions(
            lights,
            DeviceConfig.GetIntOrString(config, "colorModel", 1) == 1,
            DeviceConfig.GetIntOrString(config, "transEffect", 0) == 0,
            DeviceConfig.GetInt(config, "transTime", 50),
            DeviceConfig.GetInt(config, "extraTimeDarkness", 0),
            DeviceConfig.GetInt(config, "brightnessMin", 0),
            DeviceConfig.GetBool(config, "brightnessSwitchOffOnMinimum", true),
            DeviceConfig.GetInt(config, "brightnessMax", 100),
            DeviceConfig.GetDouble(config, "brightnessFactor", 1.0),
            DeviceConfig.GetBool(config, "restoreOriginalState", false),
            DeviceConfig.GetInt(config, "maxRetry", 60),
            TimeSpan.FromMilliseconds(DeviceConfig.GetInt(config, "quotaWait", 0)),
            TimeSpan.FromMilliseconds(500));
    }
}

/// <summary>
/// Yeelight lamps (port of <c>DriverNetYeelight</c>): every lamp gets a control connection; colors are streamed in
/// music mode, where the lamp connects to a small TCP server of ours and accepts commands without the command quota.
/// </summary>
public sealed partial class YeelightDriver : ILedDriver
{
    public const string TypeName = "yeelight";

    private static readonly TimeSpan s_streamConnectTimeout = TimeSpan.FromSeconds(1);

    private readonly YeelightOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<IPAddress?> _localAddress;
    private YeelightLight[] _lights = [];
    private TcpListener? _musicServer;
    private IPAddress _musicAddress = IPAddress.Loopback;

    public YeelightDriver(YeelightOptions options, DriverContext context, Func<IPAddress?>? localAddress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        if (options.Lights.Count == 0)
        {
            throw new ArgumentException("No Yeelight lamp is configured.", nameof(options));
        }

        _options = options;
        _time = context.Time;
        _logger = context.LoggerFactory.CreateLogger<YeelightDriver>();
        _localAddress = localAddress ?? LocalAddress;
        RetryPolicy = new RetryPolicy(Math.Max(0, options.MaxRetry), TimeSpan.FromSeconds(3), RetryPolicy.KeepTrying);
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_2_network",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new YeelightDriver(YeelightOptions.FromConfig(config), context))
    {
        Discover = request => SsdpDevices.SearchAsync(request, "wifi_bulb", 1982, "Location", "yeelight(.*)", true, (ip, _) => $"Yeelight {ip}"),
        GetProperties = GetPropertiesAsync,
        Identify = IdentifyAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy { get; }

    public string? Status { get; private set; }

    public static async Task<JsonObject> GetPropertiesAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var light = Single(request);
        return light is not null && await light.OpenAsync(request.CancellationToken) ? await light.GetPropertiesAsync(request.CancellationToken) : [];
    }

    public static async Task IdentifyAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var light = Single(request);
        if (light is not null && await light.OpenAsync(request.CancellationToken))
        {
            await light.IdentifyAsync(request.CancellationToken);
        }
    }

    /// <summary>Lamps don't need refreshing (C++: setRefreshTime(0)).</summary>
    public TimeSpan? EffectiveRefreshInterval(TimeSpan? configured) => null;

    public async ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        if (_options.Lights.Count < ledCount)
        {
            throw new InvalidOperationException($"Not enough Yeelights [{_options.Lights.Count}] for configured LEDs [{ledCount}] found!");
        }

        CloseAll();
        // Listen only on the address the lamps are told to connect to (C++ listened on every interface).
        _musicAddress = _localAddress() ?? throw new IOException("Failed to resolve an IP address for the music mode server.");
        _musicServer = new TcpListener(_musicAddress, 0);
        _musicServer.Start();
        _lights = [.. _options.Lights.Take(ledCount).Select(l => new YeelightLight(l.Host, l.Port, _options, _time, _logger))];
        var opened = 0;
        foreach (var light in _lights)
        {
            if (await light.OpenAsync(cancellationToken))
            {
                opened++;
            }
        }

        if (opened == 0)
        {
            CloseAll();
            throw new IOException("All Yeelights failed to be opened!");
        }

        if (_options.RestoreOriginalState)
        {
            foreach (var light in _lights.Where(l => !l.InError))
            {
                await light.StoreStateAsync(cancellationToken);
            }
        }

        foreach (var light in _lights.Where(l => !l.InError && !l.CheckMusicMode()))
        {
            await light.SetPowerAsync(true, smooth: true, 5000, cancellationToken);
        }

        Status = $"{opened}/{_lights.Length} lamps";
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        var count = Math.Min(colors.Length / 3, _lights.Length);
        var rgb = colors[..(count * 3)].ToArray();
        return WriteAsync(rgb).GetAwaiter().GetResult();
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        foreach (var light in _lights)
        {
            if (_options.RestoreOriginalState)
            {
                await light.RestoreStateAsync(cancellationToken);
                if (!light.WasOriginallyOn)
                {
                    await light.SetPowerAsync(false, _options.Smooth, 1000, cancellationToken);
                }
            }
            else
            {
                await light.SetPowerAsync(false, _options.Smooth, 1000, cancellationToken);
            }
        }

        CloseAll();
        Status = null;
    }

    public void Dispose() => CloseAll();

    private static YeelightLight? Single(DeviceRequest request)
    {
        var host = DeviceConfig.GetString(request.Parameters, "hostname");
        return host.Length == 0
            ? null
            : new YeelightLight(host, DeviceConfig.GetInt(request.Parameters, "port", YeelightOptions.DefaultPort), YeelightOptions.FromConfig([]), request.Context.Time,
                request.Context.LoggerFactory.CreateLogger<YeelightDriver>());
    }

    /// <summary>An IPv4 address of a running Ethernet or Wi-Fi interface (the lamps connect to it in music mode).</summary>
    private static IPAddress? LocalAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet) ||
                nic.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase) || nic.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var address = nic.GetIPProperties().UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (address is not null)
            {
                return address;
            }
        }

        return Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
    }

    private async Task<bool> WriteAsync(byte[] rgb)
    {
        var inError = 0;
        for (var i = 0; i < rgb.Length / 3; i++)
        {
            var light = _lights[i];
            if (light.InError)
            {
                inError++;
                continue;
            }

            if (!light.CheckMusicMode())
            {
                if (!await light.SetMusicModeAsync(true, _musicAddress, ((IPEndPoint)_musicServer!.LocalEndpoint).Port, CancellationToken.None))
                {
                    continue; // command quota: try again with the next frame
                }

                using var timeout = new CancellationTokenSource(s_streamConnectTimeout);
                try
                {
                    light.SetStream(await _musicServer.AcceptTcpClientAsync(timeout.Token));
                }
                catch (OperationCanceledException)
                {
                    LogNoStream(light.Host);
                    continue;
                }
            }

            var (r, g, b) = (rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2]);
            if (_options.Rgb)
            {
                await light.SetColorRgbAsync(r, g, b, CancellationToken.None);
            }
            else
            {
                await light.SetColorHsvAsync(r, g, b, CancellationToken.None);
            }
        }

        if (_lights.Length > 0 && inError >= _lights.Length)
        {
            throw new DeviceLostException("All Yeelights in error - stopping device!");
        }

        return true;
    }

    private void CloseAll()
    {
        foreach (var light in _lights)
        {
            light.Dispose();
        }

        _lights = [];
        _musicServer?.Stop();
        _musicServer?.Dispose();
        _musicServer = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Yeelight {Host} didn't open the music mode stream in time")]
    private partial void LogNoStream(string host);
}
