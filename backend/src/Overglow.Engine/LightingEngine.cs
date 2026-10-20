using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Audio;
using Overglow.Capture;
using Overglow.Capture.Hdr;
using Overglow.Core.Borders;
using Overglow.Core.Color;
using Overglow.Core.Layout;
using Overglow.Core.Mapping;
using Overglow.Core.Priorities;
using Overglow.Core.Smoothing;
using Overglow.Engine.Platform;
using Overglow.Settings;
using Overglow.Settings.Layout;

namespace Overglow.Engine;

/// <summary>
/// Wires the settings to the pipeline and the controllers: builds the color processor and LED map from the
/// <c>color</c> section and the controllers' LEDs, smoothing from <c>smoothing</c>, black border detection from
/// <c>blackborderdetector</c>, configures the controllers from <c>layout</c> (or, without one, <c>device</c> +
/// <c>leds</c>), registers the screen as a source and applies component switches. The API layer talks to this class only; it never reaches into the pipeline thread.
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
    private volatile ControllerSetup _setup;
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
        _setup = SetupFrom(settings);
        Layout = LedLayout.FromJson(_setup.AllLeds());
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
        Components.HoldChanged += OnHoldChanged;
        Sources.VisibleChanged += OnVisibleChanged;
        Sources.PrioritiesChanged += OnPrioritiesChanged;
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised when the sources or their state changed (<c>priorities-update</c>).</summary>
    public event Action? PrioritiesChanged;

    /// <summary>Raised with the new <c>color.channelAdjustment</c> array after the color settings changed.</summary>
    public event Action<JsonArray>? AdjustmentChanged;

    /// <summary>Raised with the new <c>leds</c> array (every controller's LEDs) after the layout changed.</summary>
    public event Action<JsonArray>? LedsChanged;

    /// <summary>Raised after the controllers changed: added, removed, other LEDs or driver settings.</summary>
    public event Action? ControllersChanged;

    /// <summary>Raised on a device thread when a controller changes state.</summary>
    public event Action<DeviceInfo>? DeviceChanged
    {
        add => _devices.DeviceChanged += value;
        remove => _devices.DeviceChanged -= value;
    }

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

    /// <summary>The LEDs of every controller, one controller after the other.</summary>
    public LedLayout Layout { get; private set; }

    /// <summary>The controllers and their strips, as the settings describe them.</summary>
    public ControllerSetup Setup => _setup;

    public LedMappingType MappingType => _pipeline.Config.Mapping.MappingType;

    /// <summary>Frames processed by the frame loop since start.</summary>
    public long FrameCount => _pipeline.FrameCount;

    /// <summary>Smoothing ticks that sent a frame since start.</summary>
    public long SmoothingTicks => _output.Ticks;

    public string SourceName => _source.Name;

    public string SourceDevice => _source.Device;

    /// <summary>HDR state of the screen capture; null when the frame source isn't a screen (synthetic test pattern).</summary>
    public CaptureHdrStatus? CaptureHdr => (_source as IScreenCaptureSource)?.HdrStatus;

    /// <summary>Raised on the frame loop when <see cref="CaptureHdr"/> changed.</summary>
    public event Action<CaptureHdrStatus>? CaptureHdrChanged
    {
        add
        {
            if (_source is IScreenCaptureSource screen)
            {
                screen.HdrStatusChanged += value;
            }
        }

        remove
        {
            if (_source is IScreenCaptureSource screen)
            {
                screen.HdrStatusChanged -= value;
            }
        }
    }

    public IReadOnlyList<DeviceInfo> Devices => _devices.Devices;

    public IReadOnlyList<PriorityInfo> Priorities => Sources.Priorities;

    public void Start()
    {
        lock (_applyLock)
        {
            ApplySetup();
            UpdateScreenSource();
            UpdateOutput();
        }

        _output.Start();
        _pipeline.Start();
        Effects.Start();
        LogStarted(Layout.Count, _setup.Controllers.Count, _source.Name);
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

    /// <summary>
    /// Blinks one LED (the UI's "find this LED"): LED <paramref name="ledIndex"/> of controller
    /// <paramref name="controllerId"/>, or without one the LED at that position among all controllers' LEDs.
    /// </summary>
    /// <returns>The controller and its own LED index, or null when there is no such LED or it isn't running.</returns>
    public (string ControllerId, int LedIndex)? IdentifyLed(string? controllerId, int ledIndex) => _devices.Identify(controllerId, ledIndex);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        Components.Changed -= OnComponentChanged;
        Components.HoldChanged -= OnHoldChanged;
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

    private static ControllerSetup SetupFrom(ISettingsStore settings) =>
        ControllerSetup.FromSettings(
            settings.GetSection("layout"),
            settings.GetSection("leds"),
            settings.GetSection("device"),
            settings.Controllers is [var first, ..] ? first.Name : "LED controller");

    // Per-LED brightness from the strips' brightness, or null when every LED has full brightness.
    private static float[]? GainsFor(ControllerSetup setup)
    {
        if (setup.Controllers.All(c => c.Strips.All(s => s.Brightness >= 1)))
        {
            return null;
        }

        var gains = new float[setup.LedCount];
        Array.Fill(gains, 1f);
        var start = 0;
        foreach (var controller in setup.Controllers)
        {
            foreach (var strip in controller.Strips)
            {
                var from = Math.Min(start + strip.Start, gains.Length);
                gains.AsSpan(from, Math.Min(strip.Length, gains.Length - from)).Fill((float)strip.Brightness);
            }

            start += controller.LedCount;
        }

        return gains;
    }

    private void ApplySetup()
    {
        _devices.Configure([.. _setup.Controllers.Select(c => new ControllerDevice(c.Id, c.Name, c.Device, c.LedCount, c.Enabled))]);
        _output.SetLedGains(GainsFor(_setup));
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

    private void OnHoldChanged(bool held)
    {
        lock (_applyLock)
        {
            if (!_disposed)
            {
                UpdateOutput();
            }
        }

        LogHold(held);
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
            var controllersChanged = false;
            if (change.Contains("layout") || change.Contains("leds") || change.Contains("device"))
            {
                var previous = _setup.AllLeds();
                _setup = SetupFrom(_settings);
                var allLeds = _setup.AllLeds();
                if (!JsonNode.DeepEquals(previous, allLeds))
                {
                    Layout = LedLayout.FromJson(allLeds);
                }

                ApplySetup();
                UpdateOutput();
                controllersChanged = true;
            }

            if (change.Contains("layout") || change.Contains("leds") || change.Contains("color"))
            {
                _pipeline.Configure(BuildConfig(_settings.GetSection("color"), Layout));
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

            if (controllersChanged)
            {
                ControllersChanged?.Invoke();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Lighting started: {Leds} LEDs on {Controllers} controller(s), source '{Source}'")]
    private partial void LogStarted(int leds, int controllers, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Component {Component} enabled: {On}")]
    private partial void LogComponent(Component component, bool on);

    [LoggerMessage(Level = LogLevel.Information, Message = "LED output held (nothing is sent to the controllers): {Held}")]
    private partial void LogHold(bool held);

    [LoggerMessage(Level = LogLevel.Information, Message = "Visible source: priority {Priority} ({Component})")]
    private partial void LogVisible(int priority, SourceComponent component);
}
