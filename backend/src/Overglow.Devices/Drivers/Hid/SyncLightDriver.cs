using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Devices.Transport;
using Overglow.Discovery.Usb;

namespace Overglow.Devices.Drivers.Hid;

/// <summary>Settings of the "synclight" driver (sources/led-drivers/schemas/schema-synclight.json).</summary>
/// <param name="Output">HID device path, or "auto" for the first supported device.</param>
/// <param name="PerLed">"segments" output mode: per-LED colors (else one averaged color).</param>
/// <param name="ControllerLeds">The controller's LED count; 0 reads it from the controller.</param>
/// <param name="Devices">Supported vendor/product ids (the "VID"/"PID" settings, else the two known SyncLight ids).</param>
public sealed record SyncLightOptions(
    string Output = "auto",
    byte Brightness = 255,
    bool PerLed = false,
    int ControllerLeds = 0,
    int SegmentLimit = SyncLightProtocol.MaxRanges,
    IReadOnlyList<(ushort Vendor, ushort Product)>? Devices = null)
{
    public static readonly IReadOnlyList<(ushort Vendor, ushort Product)> DefaultDevices = [(0x1a86, 0xfe07), (0x1a86, 0xfe0c)];

    public IReadOnlyList<(ushort Vendor, ushort Product)> SupportedDevices => Devices ?? DefaultDevices;

    public static SyncLightOptions FromConfig(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var output = DeviceConfig.GetString(config, "output", DeviceConfig.GetString(config, "path", "auto")).Trim();
        return new SyncLightOptions(
            output.Length == 0 ? "auto" : output,
            (byte)Math.Clamp(DeviceConfig.GetInt(config, "brightness", 255), 1, 255), // the device reads 0 as maximum
            string.Equals(DeviceConfig.GetString(config, "outputMode", "global"), "segments", StringComparison.OrdinalIgnoreCase),
            Math.Clamp(DeviceConfig.GetInt(config, "controllerLedCount", 0), 0, 254),
            Math.Clamp(DeviceConfig.GetInt(config, "controllerSegmentLimit", SyncLightProtocol.MaxRanges), 1, 254),
            ConfiguredDevices(config));
    }

    /// <summary>"VID" + "PID" ("auto" = every known device of that vendor); null when nothing valid is configured.</summary>
    private static List<(ushort, ushort)>? ConfiguredDevices(JsonObject config)
    {
        var pid = DeviceConfig.GetString(config, "PID", "auto").Trim();
        if (!TryParseId(DeviceConfig.GetString(config, "VID", "0x1a86"), out var vid))
        {
            return null;
        }

        List<(ushort, ushort)> devices = pid.Length == 0 || pid.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? [.. DefaultDevices.Where(d => d.Vendor == vid)]
            : TryParseId(pid, out var p) ? [(vid, p)] : [];
        return devices.Count > 0 ? devices : null;
    }

    /// <summary>Parses "0x1a86", "6790" or "0755" like QString::toUInt(&amp;ok, 0).</summary>
    internal static bool TryParseId(string text, out ushort value)
    {
        var t = text.Trim();
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            : t.Length > 1 && t[0] == '0'
                ? TryParseOctal(t[1..], out parsed)
                : uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
        value = ok && parsed <= 0xffff ? (ushort)parsed : (ushort)0;
        return ok && parsed <= 0xffff;
    }

    private static bool TryParseOctal(string digits, out uint value)
    {
        value = 0;
        foreach (var c in digits)
        {
            if (c is < '0' or > '7' || value > 0x1fffffff)
            {
                return false;
            }

            value = (value * 8) + (uint)(c - '0');
        }

        return digits.Length > 0;
    }
}

/// <summary>
/// Robobloq SyncLight USB HID strip. Port of <c>DriverHidSyncLight</c> and <c>ProviderHid</c>: only the vendor
/// interface (usage page 0xFF00, usage 1) of the known VID/PID pairs takes lighting commands; on open the device-side
/// effects are stopped, the brightness is set and the controller's LED count is read.
/// </summary>
public sealed partial class SyncLightDriver : ILedDriver
{
    public const string TypeName = "synclight";

    private static readonly TimeSpan s_infoTimeout = TimeSpan.FromMilliseconds(300);

    private readonly SyncLightOptions _options;
    private readonly IHidEnumerator _enumerator;
    private readonly IHidOpener _opener;
    private readonly ILogger _logger;
    private readonly SyncLightProtocol _protocol = new();
    private readonly byte[] _frame = new byte[SyncLightProtocol.MaxFrameSize];
    private readonly byte[] _report = new byte[SyncLightProtocol.ReportSize + 1];
    private IHidDevice? _device;
    private int _controllerLeds;

    public SyncLightDriver(SyncLightOptions options, DriverContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        _options = options;
        _enumerator = context.Discovery.HidDevices;
        _opener = context.Hid;
        _logger = context.LoggerFactory.CreateLogger<SyncLightDriver>();
    }

    public static DriverDescriptor Descriptor { get; } = new(
        TypeName,
        "leds_group_6_HID",
        DriverRegistry.LoadEmbeddedSchema(TypeName),
        (config, context) => new SyncLightDriver(SyncLightOptions.FromConfig(config), context))
    {
        Discover = DiscoverAsync,
    };

    public string Type => TypeName;

    public RetryPolicy RetryPolicy { get; } = RetryPolicy.Default;

    public string? Status { get; private set; }

    /// <summary>"auto" plus every supported SyncLight interface (port of <c>ProviderHid::discover</c>).</summary>
    public static Task<JsonArray> DiscoverAsync(DeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = SyncLightOptions.FromConfig(request.Parameters);
        var list = new JsonArray((JsonNode)new JsonObject { ["value"] = "auto", ["name"] = "Auto" });
        foreach (var d in Supported(request.Context.Discovery.HidDevices.GetDevices(), options))
        {
            var vid = $"0x{d.VendorId:x4}";
            var pid = $"0x{d.ProductId:x4}";
            var item = new JsonObject
            {
                ["value"] = d.Path,
                ["name"] = $"{(d.Product.Length > 0 ? d.Product : "HID device")} ({vid}:{pid})",
                ["vid"] = vid,
                ["pid"] = pid,
                ["path"] = d.Path,
            };
            if (d.Manufacturer.Length > 0)
            {
                item["manufacturer"] = d.Manufacturer;
            }

            if (d.Product.Length > 0)
            {
                item["product"] = d.Product;
            }

            if (d.Serial.Length > 0)
            {
                item["serial"] = d.Serial;
            }

            list.Add((JsonNode)item);
        }

        return Task.FromResult(list);
    }

    /// <summary>The interfaces that take lighting commands (the strip also exposes a keyboard with the same ids).</summary>
    internal static IEnumerable<HidDeviceInfo> Supported(IEnumerable<HidDeviceInfo> found, SyncLightOptions options) =>
        found.Where(d => d.UsagePage == 0xff00 && d.Usage == 1 && options.SupportedDevices.Any(s => s.Vendor == d.VendorId && s.Product == d.ProductId));

    public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
    {
        CloseDevice();
        var supported = Supported(_enumerator.GetDevices(), _options).ToList();
        var chosen = supported.Where(d => string.Equals(d.Path, _options.Output, StringComparison.OrdinalIgnoreCase)).ToList();
        string? error = null;
        foreach (var candidate in chosen.Count > 0 ? chosen : supported)
        {
            try
            {
                _device = _opener.Open(candidate.Path);
                LogOpened(candidate.Path, candidate.VendorId, candidate.ProductId);
                break;
            }
            catch (IOException ex)
            {
                error = ex.Message;
            }
        }

        if (_device is null)
        {
            throw new IOException(error ?? "No supported HID device found.");
        }

        _controllerLeds = _options.ControllerLeds;
        if (!SendRb(SyncLightProtocol.ActionStopEffect, []) || !SendBrightness())
        {
            CloseDevice();
            throw new IOException("SyncLight: the controller didn't accept the initialization.");
        }

        ReadDeviceInfo(cancellationToken);
        if (_controllerLeds == 0)
        {
            CloseDevice();
            throw new IOException("SyncLight: could not read the controller's LED count, configure it in the driver settings.");
        }

        // switchOn -> powerOn sends the brightness once more
        SendBrightness();
        Status = $"{_controllerLeds} LEDs";
        return ValueTask.CompletedTask;
    }

    public bool Write(ReadOnlySpan<byte> colors)
    {
        if (_device is null || colors.Length == 0)
        {
            return false;
        }

        var id = _protocol.NextId();
        var length = _options.PerLed
            ? _protocol.WriteSegments(_frame, colors, _controllerLeds, _options.SegmentLimit, id)
            : SyncLightProtocol.WriteGlobalColor(_frame, colors, _controllerLeds, id);
        return SendFrame(_frame.AsSpan(0, length));
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        CloseDevice();
        Status = null;
        return ValueTask.CompletedTask;
    }

    public void Dispose() => CloseDevice();

    private bool SendBrightness() => SendRb(SyncLightProtocol.ActionBrightness, [_options.Brightness]);

    private bool SendRb(byte action, ReadOnlySpan<byte> payload)
    {
        var length = SyncLightProtocol.WriteRb(_frame, action, payload, _protocol.NextId());
        return SendFrame(_frame.AsSpan(0, length));
    }

    /// <summary>Sends a frame in consecutive reports, 1 ms apart.</summary>
    private bool SendFrame(ReadOnlySpan<byte> frame)
    {
        var device = _device;
        if (device is null)
        {
            return false;
        }

        var reports = SyncLightProtocol.ReportCount(frame.Length);
        for (var i = 0; i < reports; i++)
        {
            SyncLightProtocol.WriteReport(frame, i, _report);
            if (!device.Write(_report))
            {
                return false;
            }

            if (i + 1 < reports)
            {
                Thread.Sleep(1);
            }
        }

        return true;
    }

    /// <summary>
    /// One 0x82 query; the answer is an RB report: [3] id of the query, [8] screen size in inches, [11] LED count,
    /// [21..23] firmware version. Older answers may be queued in front of it.
    /// </summary>
    private void ReadDeviceInfo(CancellationToken cancellationToken)
    {
        var id = _protocol.NextId();
        var length = SyncLightProtocol.WriteRb(_frame, SyncLightProtocol.ActionReadInfo, [], id);
        if (!SendFrame(_frame.AsSpan(0, length)))
        {
            return;
        }

        Span<byte> reply = stackalloc byte[SyncLightProtocol.ReportSize];
        int n;
        while (!cancellationToken.IsCancellationRequested && (n = _device!.Read(reply, s_infoTimeout)) > 0)
        {
            if (n >= 24 && reply[3] == id)
            {
                if (_controllerLeds == 0)
                {
                    _controllerLeds = Math.Clamp((int)reply[11], 1, 254);
                }

                LogDeviceInfo(reply[8], reply[11], reply[21], reply[22], reply[23]);
                return;
            }
        }

        LogNoDeviceInfo();
    }

    private void CloseDevice()
    {
        _device?.Dispose();
        _device = null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened HID device {Path} VID=0x{Vendor:x4} PID=0x{Product:x4}")]
    private partial void LogOpened(string path, ushort vendor, ushort product);

    [LoggerMessage(Level = LogLevel.Information, Message = "SyncLight: {Inches}\" display, controller reports {Leds} LEDs, firmware {Major}.{Minor}.{Patch}")]
    private partial void LogDeviceInfo(byte inches, byte leds, byte major, byte minor, byte patch);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SyncLight: no answer to the device info query, continuing")]
    private partial void LogNoDeviceInfo();
}
