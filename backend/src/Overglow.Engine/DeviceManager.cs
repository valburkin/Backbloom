using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Core.Color;
using Overglow.Devices;
using Overglow.Settings.Layout;

namespace Overglow.Engine;

/// <summary>One controller to drive: its driver settings and how many of the pipeline's LEDs are its own.</summary>
/// <param name="Id">The layout's controller id.</param>
/// <param name="Device">The driver settings (<c>type</c> + driver settings); null or without a type: no output.</param>
/// <param name="Enabled">False: the controller keeps its LEDs in the pipeline but nothing is sent to it.</param>
public sealed record ControllerDevice(string Id, string Name, JsonObject? Device, int LedCount, bool Enabled = true);

/// <summary>Live information about one controller.</summary>
/// <param name="Start">Index of the controller's first LED among the pipeline's LEDs.</param>
/// <param name="Type">The driver type ("" when none is configured).</param>
/// <param name="Running">False when nothing is sent to it (disabled, or its driver settings are unusable: see <paramref name="Status"/>).</param>
public sealed record DeviceInfo(
    string Id,
    string Name,
    string Type,
    int Start,
    int LedCount,
    bool Running,
    DeviceState State,
    string? Status,
    FrameStatsSnapshot Stats);

/// <summary>
/// Owns the LED device runners and fans every processed frame out to them: controller N gets the slice of the
/// pipeline's colors that follows controllers 0..N-1 (one mapping pass, many controllers, one clock).
/// </summary>
/// <remarks>
/// Each runner has its own thread; <see cref="Submit"/> only copies a slice into the runner's buffer, so a slow,
/// failing or retrying controller never delays the frame loop or the other controllers. Reconfiguring keeps the
/// runners whose driver settings and LED count are unchanged (no reconnect, no blackout when a strip is edited).
/// </remarks>
public sealed partial class DeviceManager : IDisposable
{
    private readonly DriverRegistry _registry;
    private readonly DriverContext _context;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private volatile Slot[] _slots = [];
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

    /// <summary>Every configured controller, in LED order.</summary>
    public IReadOnlyList<DeviceInfo> Devices => [.. _slots.Select(s => s.Describe())];

    /// <summary>All LEDs of the controllers together.</summary>
    public int LedCount => _slots is [.., var last] ? last.Start + last.LedCount : 0;

    /// <summary>One controller for <paramref name="ledCount"/> LEDs (a configuration without a layout).</summary>
    public void Configure(JsonObject? device, int ledCount) =>
        Configure([new ControllerDevice(ControllerSetup.LegacyId, "LED controller", device, ledCount)]);

    /// <summary>
    /// Replaces the controllers. Runners with the same id, name, driver settings and LED count are kept (their position
    /// may change); the others are switched off (black frame, power off) and new ones are started.
    /// </summary>
    public void Configure(IReadOnlyList<ControllerDevice> controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        lock (_lock)
        {
            var old = _slots;
            var reusable = old.Where(s => s.Runner is not null).ToList();
            var slots = new Slot[controllers.Count];
            var start = 0;
            for (var i = 0; i < controllers.Count; i++)
            {
                var controller = controllers[i];
                var key = Key(controller);
                var kept = reusable.FindIndex(s => s.Key == key);
                if (kept >= 0)
                {
                    slots[i] = reusable[kept].MovedTo(start);
                    reusable.RemoveAt(kept);
                }
                else
                {
                    slots[i] = Create(controller, key, start);
                }

                start += Math.Max(0, controller.LedCount);
            }

            _slots = slots;
            foreach (var slot in reusable)
            {
                slot.Runner!.Dispose();
            }

            foreach (var slot in slots)
            {
                slot.Runner?.SetEnabled(_enabled);
            }
        }
    }

    /// <summary>Switches every controller on or off.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _enabled = enabled;
            foreach (var slot in _slots)
            {
                slot.Runner?.SetEnabled(enabled);
            }
        }
    }

    /// <summary>
    /// Hands a processed frame to every controller: each gets its slice (LEDs the frame doesn't reach are black).
    /// Called from the frame loop or the smoothing thread; does not allocate.
    /// </summary>
    public void Submit(ReadOnlySpan<Vector3> colors)
    {
        foreach (var slot in _slots)
        {
            if (slot.Runner is { } runner)
            {
                var start = Math.Min(slot.Start, colors.Length);
                runner.Submit(colors.Slice(start, Math.Min(slot.LedCount, colors.Length - start)));
            }
        }
    }

    /// <summary>
    /// Blinks one LED (the UI's "find this LED"): LED <paramref name="ledIndex"/> of the controller with id
    /// <paramref name="controllerId"/>, or, without an id, the LED at that position among all controllers' LEDs.
    /// </summary>
    /// <returns>The controller and its own LED index, or null when there is no such LED or the controller isn't running.</returns>
    public (string ControllerId, int LedIndex)? Identify(string? controllerId, int ledIndex)
    {
        foreach (var slot in _slots)
        {
            var local = controllerId is null ? ledIndex - slot.Start : ledIndex;
            if ((controllerId is null || slot.Id == controllerId) && (uint)local < (uint)slot.LedCount)
            {
                if (slot.Runner is not { } runner)
                {
                    return null;
                }

                runner.Identify(local);
                return (slot.Id, local);
            }
        }

        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var slot in _slots)
            {
                slot.Runner?.Dispose();
            }

            _slots = [];
        }
    }

    private static string Key(ControllerDevice c) =>
        $"{c.Id}\n{c.Name}\n{c.LedCount}\n{c.Enabled}\n{c.Device?.ToJsonString() ?? string.Empty}";

    private Slot Create(ControllerDevice controller, string key, int start)
    {
        var device = controller.Device;
        var type = device is null ? null : DeviceConfig.GetString(device, "type");
        Slot Idle(string status) => new(controller, key, start, type ?? string.Empty, null, status, this);
        if (!controller.Enabled)
        {
            return Idle("Disabled");
        }

        if (device is null || string.IsNullOrEmpty(type))
        {
            LogNoDevice(controller.Name);
            return Idle("No LED driver is configured");
        }

        if (!_registry.TryGet(type, out var descriptor))
        {
            LogUnknownType(controller.Name, type);
            return Idle($"The '{type}' driver isn't available in this version");
        }

        ILedDriver driver;
        try
        {
            driver = descriptor.Create(device, _context);
        }
        catch (ArgumentException ex)
        {
            LogInvalidConfig(controller.Name, type, ex.Message);
            return Idle($"The driver settings are incomplete: {ex.Message}");
        }

        var refresh = DeviceConfig.GetInt(device, "refreshTime", 0);
        var options = new LedDeviceRunnerOptions(
            controller.Name,
            Math.Max(0, controller.LedCount),
            ColorOrders.Parse(DeviceConfig.GetString(device, "colorOrder", "rgb")),
            refresh > 0 ? TimeSpan.FromMilliseconds(refresh) : null);
        var runner = new LedDeviceRunner(driver, options, _loggerFactory.CreateLogger($"Overglow.Devices.{type}"));
        var slot = new Slot(controller, key, start, type, runner, null, this);
        runner.StateChanged += (_, _) => DeviceChanged?.Invoke(slot.Current().Describe());
        runner.Start();
        return slot;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Controller}: no LED device is configured")]
    private partial void LogNoDevice(string controller);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Controller}: LED device type '{Type}' isn't available in this version")]
    private partial void LogUnknownType(string controller, string type);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Controller}: the '{Type}' LED device settings are incomplete: {Reason}")]
    private partial void LogInvalidConfig(string controller, string type, string reason);

    /// <summary>A controller at its place among the pipeline's LEDs; immutable, replaced when the place changes.</summary>
    private sealed class Slot(ControllerDevice controller, string key, int start, string type, LedDeviceRunner? runner, string? problem, DeviceManager owner)
    {
        public string Key { get; } = key;

        public string Id => controller.Id;

        public int Start { get; } = start;

        public int LedCount { get; } = Math.Max(0, controller.LedCount);

        public LedDeviceRunner? Runner { get; } = runner;

        public Slot MovedTo(int newStart) => newStart == Start ? this : new Slot(controller, Key, newStart, type, Runner, problem, owner);

        // The slot that holds this runner now (it may have moved since the runner was created).
        public Slot Current() => owner._slots.FirstOrDefault(s => ReferenceEquals(s.Runner, Runner) && Runner is not null) ?? this;

        public DeviceInfo Describe() => Runner is { } r
            ? new DeviceInfo(controller.Id, controller.Name, r.Type, Start, LedCount, true, r.State, r.Status, r.Stats.Snapshot())
            : new DeviceInfo(controller.Id, controller.Name, type, Start, LedCount, false, DeviceState.Off, problem, default);
    }
}
