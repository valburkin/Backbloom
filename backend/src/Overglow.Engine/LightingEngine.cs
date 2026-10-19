using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Capture;
using Overglow.Core.Color;
using Overglow.Core.Layout;
using Overglow.Core.Mapping;
using Overglow.Settings;

namespace Overglow.Engine;

/// <summary>A source competing for the LEDs, as listed in <c>serverinfo.priorities</c>.</summary>
public sealed record PriorityInfo(int Priority, string ComponentId, string Origin, string? Owner, bool Active, bool Visible);

/// <summary>
/// Wires the settings to the pipeline and the controllers: builds the color processor and LED map from the
/// <c>color</c> and <c>leds</c> sections, (re)creates controllers from <c>device</c>, and applies component switches.
/// The API layer talks to this class only; it never reaches into the pipeline thread.
/// </summary>
public sealed partial class LightingEngine : IDisposable
{
    /// <summary>Priority of the screen source (HyperHDR's default <c>systemControl.systemInstancePriority</c>).</summary>
    public const int ScreenPriority = 245;

    private readonly ISettingsStore _settings;
    private readonly IFrameSource _source;
    private readonly DeviceManager _devices;
    private readonly LightingPipeline _pipeline;
    private readonly ILogger _logger;
    private readonly Lock _applyLock = new();
    private bool _disposed;

    public LightingEngine(ISettingsStore settings, IFrameSource source, DeviceManager devices, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _settings = settings;
        _source = source;
        _devices = devices;
        _logger = loggerFactory.CreateLogger<LightingEngine>();

        var general = settings.GetSection("general") as JsonObject;
        Components = new ComponentStates(ledDeviceOn: !(general?["disableLedsStartup"] is JsonValue v && v.TryGetValue(out bool off) && off));
        Layout = LedLayout.FromJson(settings.GetSection("leds"));
        _pipeline = new LightingPipeline(source, Components, devices, LedColors, BuildConfig(settings.GetSection("color"), Layout), loggerFactory.CreateLogger<LightingPipeline>());

        Components.Changed += OnComponentChanged;
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised when the visible sources change (screen source switched on or off).</summary>
    public event Action? PrioritiesChanged;

    /// <summary>Raised with the new <c>color.channelAdjustment</c> array after the color settings changed.</summary>
    public event Action<JsonArray>? AdjustmentChanged;

    /// <summary>Raised with the new <c>leds</c> array after the layout changed.</summary>
    public event Action<JsonArray>? LedsChanged;

    /// <summary>Raised with "advanced" / "unicolor_mean" after the mapping type changed.</summary>
    public event Action<string>? MappingTypeChanged;

    public ComponentStates Components { get; }

    public LedColorsFeed LedColors { get; } = new();

    public LedLayout Layout { get; private set; }

    public LedMappingType MappingType => _pipeline.Config.Mapping.MappingType;

    /// <summary>Frames processed by the frame loop since start.</summary>
    public long FrameCount => _pipeline.FrameCount;

    public string SourceName => _source.Name;

    public string SourceDevice => _source.Device;

    public IReadOnlyList<DeviceInfo> Devices => _devices.Devices;

    public IReadOnlyList<PriorityInfo> Priorities => Components.CaptureActive
        ? [new PriorityInfo(ScreenPriority, Component.SystemGrabber.ToId(), "System", _source.Name, Active: true, Visible: true)]
        : [];

    public void Start()
    {
        _devices.Configure(_settings.GetSection("device") as JsonObject, Layout.Count);
        _devices.SetEnabled(Components.OutputActive);
        _pipeline.Start();
        LogStarted(Layout.Count, _source.Name);
    }

    /// <summary>Blinks one LED on every controller (the UI's "find this LED").</summary>
    public void IdentifyLed(int ledIndex) => _devices.Identify(ledIndex);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        Components.Changed -= OnComponentChanged;
        _pipeline.Dispose();
        _devices.Dispose();
    }

    private static PipelineConfig BuildConfig(JsonNode? color, LedLayout layout)
    {
        var adjustment = ColorAdjustment.FromJson((color?["channelAdjustment"] as JsonArray)?.FirstOrDefault());
        var mappingType = LedMappingTypes.Parse(color?["imageToLedMappingType"] is JsonValue t && t.TryGetValue(out string? s) ? s : null);
        var sparse = color?["sparse_processing"] is JsonValue sp && sp.TryGetValue(out bool b) && b;
        return new PipelineConfig(new LedMappingOptions(layout, mappingType, sparse), new ColorProcessor(adjustment));
    }

    private void OnComponentChanged(Component component, bool on)
    {
        _devices.SetEnabled(Components.OutputActive);
        if (component is Component.All or Component.SystemGrabber)
        {
            PrioritiesChanged?.Invoke();
        }

        LogComponent(component, on);
    }

    private void OnSettingsChanged(SettingsChange change)
    {
        lock (_applyLock)
        {
            if (_disposed)
            {
                return;
            }

            var previousType = _pipeline.Config.Mapping.MappingType;
            if (change.Contains("leds"))
            {
                Layout = LedLayout.FromJson(change.Sections["leds"]);
            }

            if (change.Contains("leds") || change.Contains("color"))
            {
                _pipeline.Configure(BuildConfig(_settings.GetSection("color"), Layout));
            }

            if (change.Contains("leds") || change.Contains("device"))
            {
                _devices.Configure(_settings.GetSection("device") as JsonObject, Layout.Count);
            }

            if (change.Sections.TryGetValue("color", out var color) && color["channelAdjustment"] is JsonArray adjustments)
            {
                AdjustmentChanged?.Invoke(adjustments.DeepClone().AsArray());
            }

            if (change.Sections.TryGetValue("leds", out var leds) && leds is JsonArray ledArray)
            {
                LedsChanged?.Invoke(ledArray.DeepClone().AsArray());
            }

            var type = _pipeline.Config.Mapping.MappingType;
            if (type != previousType)
            {
                MappingTypeChanged?.Invoke(type.ToSettingString());
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Lighting started: {Leds} LEDs, source '{Source}'")]
    private partial void LogStarted(int leds, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Component {Component} enabled: {On}")]
    private partial void LogComponent(Component component, bool on);
}
