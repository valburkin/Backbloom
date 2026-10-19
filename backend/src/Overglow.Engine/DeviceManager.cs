using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Core.Color;
using Overglow.Devices;

namespace Overglow.Engine;

/// <summary>Live information about one controller.</summary>
public sealed record DeviceInfo(string Name, string Type, int LedCount, DeviceState State, string? Status, FrameStatsSnapshot Stats);

/// <summary>
/// Owns the LED device runners and fans every processed frame out to them. Today the settings describe one controller
/// (the HyperHDR <c>device</c> section); the list form is ready for several controllers fed by one pipeline.
/// </summary>
public sealed partial class DeviceManager : IDisposable
{
    private readonly DriverRegistry _registry;
    private readonly DriverContext _context;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private volatile LedDeviceRunner[] _runners = [];
    private bool _enabled;

    public DeviceManager(DriverRegistry registry, DriverContext context, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _registry = registry;
        _context = context;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DeviceManager>();
    }

    /// <summary>Raised on a device thread when a controller changes state.</summary>
    public event Action<DeviceInfo>? DeviceChanged;

    public IReadOnlyList<DeviceInfo> Devices => [.. _runners.Select(Describe)];

    /// <summary>
    /// Replaces the controllers with the ones described by <paramref name="device"/> (the <c>device</c> section) for
    /// <paramref name="ledCount"/> LEDs. The old controllers are switched off first.
    /// </summary>
    public void Configure(JsonObject? device, int ledCount)
    {
        lock (_lock)
        {
            var old = _runners;
            _runners = [];
            foreach (var runner in old)
            {
                runner.Dispose();
            }

            var created = Create(device, ledCount);
            if (created is not null)
            {
                created.StateChanged += (r, _) => DeviceChanged?.Invoke(Describe(r));
                created.Start();
                created.SetEnabled(_enabled);
                _runners = [created];
            }
        }
    }

    /// <summary>Switches every controller on or off.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _enabled = enabled;
            foreach (var runner in _runners)
            {
                runner.SetEnabled(enabled);
            }
        }
    }

    /// <summary>Hands a processed frame to every controller. Called from the frame loop; does not allocate.</summary>
    public void Submit(ReadOnlySpan<Vector3> colors)
    {
        foreach (var runner in _runners)
        {
            runner.Submit(colors);
        }
    }

    public void Identify(int ledIndex)
    {
        foreach (var runner in _runners)
        {
            runner.Identify(ledIndex);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var runner in _runners)
            {
                runner.Dispose();
            }

            _runners = [];
        }
    }

    private static DeviceInfo Describe(LedDeviceRunner r) => new(r.Name, r.Type, r.LedCount, r.State, r.Status, r.Stats.Snapshot());

    private LedDeviceRunner? Create(JsonObject? device, int ledCount)
    {
        var type = device is null ? null : DeviceConfig.GetString(device, "type");
        if (device is null || string.IsNullOrEmpty(type))
        {
            LogNoDevice();
            return null;
        }

        if (!_registry.TryGet(type, out var descriptor))
        {
            LogUnknownType(type);
            return null;
        }

        ILedDriver driver;
        try
        {
            driver = descriptor.Create(device, _context);
        }
        catch (ArgumentException ex)
        {
            LogInvalidConfig(type, ex.Message);
            return null;
        }

        var refresh = DeviceConfig.GetInt(device, "refreshTime", 0);
        var options = new LedDeviceRunnerOptions(
            type,
            ledCount,
            ColorOrders.Parse(DeviceConfig.GetString(device, "colorOrder", "rgb")),
            refresh > 0 ? TimeSpan.FromMilliseconds(refresh) : null);
        return new LedDeviceRunner(driver, options, _loggerFactory.CreateLogger($"Overglow.Devices.{type}"));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No LED device is configured")]
    private partial void LogNoDevice();

    [LoggerMessage(Level = LogLevel.Error, Message = "LED device type '{Type}' isn't available in this version")]
    private partial void LogUnknownType(string type);

    [LoggerMessage(Level = LogLevel.Error, Message = "The '{Type}' LED device settings are incomplete: {Reason}")]
    private partial void LogInvalidConfig(string type, string reason);
}
