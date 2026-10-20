using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Audio;
using Overglow.Capture;
using Overglow.Core.Borders;
using Overglow.Core.Color;
using Overglow.Core.Layout;
using Overglow.Core.Mapping;
using Overglow.Core.Priorities;
using Overglow.Core.Smoothing;
using Overglow.Engine.Platform;
using Overglow.Settings;

namespace Overglow.Engine;

/// <summary>
/// Wires the settings to the pipeline and the controllers: builds the color processor and LED map from the
/// <c>color</c> and <c>leds</c> sections, smoothing from <c>smoothing</c>, black border detection from
/// <c>blackborderdetector</c>, (re)creates controllers from <c>device</c>, registers the screen as a source and
/// applies component switches. The API layer talks to this class only; it never reaches into the pipeline thread.
/// </summary>
public sealed partial class LightingEngine : IDisposable
{
    /// <summary>Priority of the screen source unless <c>systemControl.systemInstancePriority</c> says otherwise.</summary>
    public const int DefaultScreenPriority = 245;

    private readonly ISettingsStore _settings;
    private readonly IFrameSource _source;
    private readonly DeviceManager _devices;
    private readonly OutputStage _output;
    private readonly LightingPipeline _pipeline;
    private readonly ScreenActivity _screen;
    private readonly ILogger _logger;
    private readonly Lock _applyLock = new();
    private int _screenPriority;
    private bool _screenRegistered;
    private bool _disposed;

    /// <param name="sound">Music mode's sound capture; null where there is none (music effects stay dark).</param>
    public LightingEngine(ISettingsStore settings, IFrameSource source, DeviceManager devices, IClock clock, ILoggerFactory loggerFactory, SoundCapture? sound = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _settings = settings;
        _source = source;
        _devices = devices;
        _logger = loggerFactory.CreateLogger<LightingEngine>();

        var general = settings.GetSection("general") as JsonObject;
        var smoothing = SmoothingSettings.FromJson(settings.GetSection("smoothing"));
        Components = new ComponentStates(
            ledDeviceOn: !(general?["disableLedsStartup"] is JsonValue v && v.TryGetValue(out bool off) && off),
            smoothingOn: smoothing.Enable,
            blackBorderOn: BorderEnabled(settings.GetSection("blackborderdetector")));
        Layout = LedLayout.FromJson(settings.GetSection("leds"));
        Sources = new PriorityManager(clock);
        _output = new OutputStage(devices, LedColors, clock, loggerFactory.CreateLogger<OutputStage>());
        _output.Configure(smoothing);
        _screen = new ScreenActivity(Sources, clock);
        _pipeline = new LightingPipeline(
            source, Components, () => Sources.Visible, _output, BuildConfig(settings.GetSection("color"), Layout), loggerFactory.CreateLogger<LightingPipeline>(), _screen);
        _pipeline.ConfigureBorders(BorderOptions());
        _screenPriority = ScreenPriorityFrom(settings.GetSection("systemControl"));
        Sound = sound;
        Effects = new EffectEngine(Sources, Components, settings, AddSmoothingConfig, clock, sound, loggerFactory.CreateLogger<EffectEngine>());

        Components.Changed += OnComponentChanged;
        Sources.VisibleChanged += OnVisibleChanged;
        Sources.PrioritiesChanged += OnPrioritiesChanged;
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised when the sources or their state changed (<c>priorities-update</c>).</summary>
    public event Action? PrioritiesChanged;

    /// <summary>Raised with the new <c>color.channelAdjustment</c> array after the color settings changed.</summary>
    public event Action<JsonArray>? AdjustmentChanged;

    /// <summary>Raised with the new <c>leds</c> array after the layout changed.</summary>
    public event Action<JsonArray>? LedsChanged;

    /// <summary>Raised with "advanced" / "unicolor_mean" after the mapping type changed.</summary>
    public event Action<string>? MappingTypeChanged;

    public ComponentStates Components { get; }

    /// <summary>
    /// The sources competing for the LEDs: the screen, colors, and effects (an <see cref="ILightSource"/> with a
    /// smoothing configuration from <see cref="AddSmoothingConfig"/>).
    /// </summary>
    public PriorityManager Sources { get; }

    /// <summary>Effects and music mode: running effects, the startup and background effects.</summary>
    public EffectEngine Effects { get; }

    /// <summary>Music mode's sound capture, or null where there is none.</summary>
    public SoundCapture? Sound { get; }

    public LedColorsFeed LedColors { get; } = new();

    public LedLayout Layout { get; private set; }

    public LedMappingType MappingType => _pipeline.Config.Mapping.MappingType;

    /// <summary>Frames processed by the frame loop since start.</summary>
    public long FrameCount => _pipeline.FrameCount;

    /// <summary>Smoothing ticks that sent a frame since start.</summary>
    public long SmoothingTicks => _output.Ticks;

    public string SourceName => _source.Name;

    public string SourceDevice => _source.Device;

    public IReadOnlyList<DeviceInfo> Devices => _devices.Devices;

    public IReadOnlyList<PriorityInfo> Priorities => Sources.Priorities;

    public void Start()
    {
        _devices.Configure(_settings.GetSection("device") as JsonObject, Layout.Count);
        lock (_applyLock)
        {
            UpdateScreenSource();
            UpdateOutput();
        }

        _output.Start();
        _pipeline.Start();
        Effects.Start();
        LogStarted(Layout.Count, _source.Name);
    }

    /// <summary>Runs an effect (the <c>effect</c> command) for <paramref name="durationMs"/> ms (&lt;= 0: until cleared).</summary>
    /// <returns>False when there is no such effect.</returns>
    public bool RunEffect(string name, int priority, long durationMs, string origin) => Effects.Run(name, priority, durationMs, origin);

    /// <summary>Shows a static color (the <c>color</c> command) for <paramref name="durationMs"/> ms (&lt;= 0: until cleared).</summary>
    public void SetColor(int priority, Rgb8 color, long durationMs, string origin) => Sources.SetColor(priority, color, durationMs, origin);

    /// <summary>Removes a source; a negative priority removes every color and effect.</summary>
    public bool Clear(int priority)
    {
        if (priority < 0)
        {
            Sources.ClearAll();
            return true;
        }

        return Sources.Clear(priority);
    }

    /// <summary>
    /// Registers a smoothing configuration for an effect (or replaces the one with <paramref name="configId"/>) and
    /// returns its id, for <see cref="PriorityManager.Register"/>.
    /// </summary>
    /// <param name="pause">The effect's colors are shown directly.</param>
    public int AddSmoothingConfig(int configId, long settlingTimeMs, double updateFrequency, bool pause) =>
        _output.AddSmoothingConfig(configId, settlingTimeMs, updateFrequency, pause);

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
        Sources.VisibleChanged -= OnVisibleChanged;
        Sources.PrioritiesChanged -= OnPrioritiesChanged;
        _pipeline.Dispose();
        Effects.Dispose();
        _output.Dispose();
        Sources.Dispose();
        _devices.Dispose();
    }

    private static PipelineConfig BuildConfig(JsonNode? color, LedLayout layout)
    {
        var adjustment = ColorAdjustment.FromJson((color?["channelAdjustment"] as JsonArray)?.FirstOrDefault());
        var mappingType = LedMappingTypes.Parse(color?["imageToLedMappingType"] is JsonValue t && t.TryGetValue(out string? s) ? s : null);
        var sparse = color?["sparse_processing"] is JsonValue sp && sp.TryGetValue(out bool b) && b;
        return new PipelineConfig(new LedMappingOptions(layout, mappingType, sparse), new ColorProcessor(adjustment));
    }

    // BlackBorderProcessor::handleSettingsUpdate reads "enable" with toBool(true); the schema default is false.
    private static bool BorderEnabled(JsonNode? section) => section?["enable"] is not JsonValue v || !v.TryGetValue(out bool on) || on;

    private static int ScreenPriorityFrom(JsonNode? systemControl) =>
        systemControl?["systemInstancePriority"] is JsonValue v && v.TryGetValue(out double d) ? (int)d : DefaultScreenPriority;

    private BlackBorderOptions? BorderOptions() =>
        Components.IsOn(Component.BlackBorder) ? BlackBorderOptions.FromJson(_settings.GetSection("blackborderdetector")) : null;

    private void OnComponentChanged(Component component, bool on)
    {
        lock (_applyLock)
        {
            switch (component)
            {
                case Component.Smoothing:
                    _output.SetSmoothingEnabled(on);
                    _pipeline.Refresh();
                    break;
                case Component.BlackBorder:
                    _pipeline.ConfigureBorders(BorderOptions());
                    break;
                case Component.All:
                case Component.SystemGrabber:
                    UpdateScreenSource();
                    break;
            }

            UpdateOutput();
        }

        LogComponent(component, on);
    }

    private void OnVisibleChanged(VisibleSource visible)
    {
        lock (_applyLock)
        {
            // HyperHDR selected the source's smoothing configuration on every LED update; here only when it changes
            _output.UseSmoothingConfig(visible.SmoothingConfig);
            UpdateOutput();
        }

        _pipeline.Interrupt();
        LogVisible(visible.Priority, visible.Component);
    }

    private void OnPrioritiesChanged() => PrioritiesChanged?.Invoke();

    // SystemControl: the screen is a source while capture runs; it is active while it delivers frames (ScreenActivity).
    private void UpdateScreenSource()
    {
        var wanted = Components.CaptureActive;
        if (wanted == _screenRegistered)
        {
            return;
        }

        _screenRegistered = wanted;
        if (wanted)
        {
            Sources.Register(_screenPriority, SourceComponent.SystemGrabber, "System", _source.Name);
            _screen.Registered(_screenPriority);
            _pipeline.Wake();
        }
        else
        {
            _screen.Unregistered();
            Sources.Clear(_screenPriority);
        }
    }

    // The controllers run while the output is on and something is visible ("no source left": HyperHDR switched the
    // LED device off; here the LEDDEVICE switch keeps the user's choice).
    private void UpdateOutput()
    {
        var on = Components.OutputActive && Sources.Visible.HasSource;
        _devices.SetEnabled(on);
        if (_output.SetOutputEnabled(on))
        {
            _pipeline.Refresh();
        }
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
                UpdateOutput();
            }

            if (change.Sections.TryGetValue("smoothing", out var smoothing))
            {
                var parsed = SmoothingSettings.FromJson(smoothing);
                Components.Set(Component.Smoothing, parsed.Enable);
                _output.Configure(parsed);
                _pipeline.Refresh();
                
            }

            if (change.Sections.TryGetValue("blackborderdetector", out var border))
            {
                Components.Set(Component.BlackBorder, BorderEnabled(border));
                _pipeline.ConfigureBorders(BorderOptions());
            }

            if (change.Sections.TryGetValue("systemControl", out var systemControl) && ScreenPriorityFrom(systemControl) != _screenPriority)
            {
                if (_screenRegistered)
                {
                    _screen.Unregistered();
                    Sources.Clear(_screenPriority);
                    _screenRegistered = false;
                }

                _screenPriority = ScreenPriorityFrom(systemControl);
                UpdateScreenSource();
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Visible source: priority {Priority} ({Component})")]
    private partial void LogVisible(int priority, SourceComponent component);
}
