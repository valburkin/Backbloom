using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Audio;
using Backbloom.Capture;
using Backbloom.Capture.Hdr;
using Backbloom.Core.Borders;
using Backbloom.Core.Color;
using Backbloom.Core.Effects;
using Backbloom.Core.Layout;
using Backbloom.Core.Mapping;
using Backbloom.Core.Priorities;
using Backbloom.Core.Smoothing;
using Backbloom.Devices;
using Backbloom.Engine.Platform;
using Backbloom.Settings;
using Backbloom.Settings.Layout;

namespace Backbloom.Engine;

/// <summary>
/// Wires the settings to the pipeline and the controllers: builds the color processor and LED map from the
/// <c>color</c> section and the controllers' LEDs, smoothing from <c>smoothing</c>, black border detection from
/// <c>blackborderdetector</c>, configures the controllers from <c>layout</c> (or, without one, <c>device</c> +
/// <c>leds</c>), plays the layout's zones (<see cref="ZoneLayer"/>), registers the screen as a source and applies
/// component switches. A profile of the layout (<see cref="ApplyProfile"/>) switches the compiled setup without saving
/// anything. The API layer talks to this class only; it never reaches into the pipeline thread.
/// </summary>
public sealed partial class LightingEngine : IDisposable
{
    /// <summary>Priority of the screen source unless <c>systemControl.systemInstancePriority</c> says otherwise.</summary>
    public const int DefaultScreenPriority = 245;

    /// <summary>
    /// How long the controllers stay connected (black) when nothing is left to show while the output is on. Not in the
    /// C++ code, which switched them off at once: a display change stops the capture for a second, and reconnecting a
    /// network controller then can fail for minutes.
    /// </summary>
    public static readonly TimeSpan NoSourceOffDelay = TimeSpan.FromSeconds(10);

    private readonly ISettingsStore _settings;
    private readonly IFrameSource _source;
    private readonly DeviceManager _devices;
    private readonly OutputStage _output;
    private readonly LightingPipeline _pipeline;
    private readonly ScreenActivity _screen;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Lock _applyLock = new();
    private readonly HashSet<string> _unknownZoneEffects = new(StringComparer.Ordinal);
    private volatile ControllerSetup _setup;
    private ZoneLayer _zones = ZoneLayer.Empty;
    private string? _profileId;
    private int _screenPriority;
    private bool _screenRegistered;
    private bool _started;
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
        _clock = clock;
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
            source, Components, () => Sources.Visible, _output, BuildConfig(settings.GetSection("color"), Layout), loggerFactory.CreateLogger<LightingPipeline>(), _screen, clock);
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

    /// <summary>The controllers and their strips, as the settings describe them (compiled for the active profile).</summary>
    public ControllerSetup Setup => _setup;

    /// <summary>The zones played now (see <see cref="ControllerSetup.Zones"/>).</summary>
    public ZoneLayer Zones => _pipeline.Zones;

    /// <summary>The profile whose setup runs (<see cref="ApplyProfile"/>), or null: the layout as saved.</summary>
    public string? ProfileId => _setup.ProfileId;

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

    /// <summary>The highest values of one frame of a controller since the previous call (they are reset: the performance recorder's).</summary>
    internal FramePeaks? TakePeaks(string controllerId) => _devices.TakePeaks(controllerId);

    public IReadOnlyList<PriorityInfo> Priorities => Sources.Priorities;

    public void Start()
    {
        lock (_applyLock)
        {
            _started = true;
            ApplySetup();
            UpdateScreenSource();
            UpdateOutput();
        }

        _output.Start();
        _pipeline.Start();
        Effects.Start();
        LogStarted(Layout.Count, _setup.Controllers.Count, _source.Name);
    }

    /// <summary>
    /// Runs the setup the profile <paramref name="profileId"/> of the layout describes (other roles for the strips'
    /// sections: rects and zones), or the layout as saved (null, or an id the layout doesn't have). Nothing is saved;
    /// controllers whose driver settings and LED count stay the same keep running (no reconnect). Later layout changes
    /// are compiled for the same profile.
    /// </summary>
    public void ApplyProfile(string? profileId)
    {
        lock (_applyLock)
        {
            if (_disposed || profileId == _profileId)
            {
                return;
            }

            _profileId = profileId;
            if (Recompile())
            {
                _pipeline.Configure(BuildConfig(_settings.GetSection("color"), Layout));
            }
        }

        LogProfile(profileId ?? "(none)");
    }

    /// <summary>Runs an effect (the <c>effect</c> command) for <paramref name="durationMs"/> ms (&lt;= 0: until cleared).</summary>
    /// <returns>False when there is no such effect.</returns>
    public EffectStart RunEffect(string name, int priority, long durationMs, string origin) => Effects.Run(name, priority, durationMs, origin);

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
    /// <paramref name="controllerId"/>, or without one the LED at that position among all controllers' LEDs, replacing
    /// any other blink. <paramref name="hold"/> keeps it on (cycling red, green, blue) until <see cref="StopIdentify"/>,
    /// the next blink or a 120 s timeout. It also shows while the lights are switched off or nothing is visible (the
    /// controller opens for it), but never while the output is held (<see cref="ComponentStates.OutputHeld"/>); switching
    /// the lights off or holding the output ends it.
    /// </summary>
    /// <returns>The controller and its own LED index, or null when there is no such LED, it isn't running or the output is held.</returns>
    public (string ControllerId, int LedIndex)? IdentifyLed(string? controllerId, int ledIndex, bool hold = false)
    {
        // under the lock, so a hold that starts meanwhile (OnHoldChanged) ends this blink
        lock (_applyLock)
        {
            return Components.OutputHeld ? null : _devices.Identify(controllerId, ledIndex, hold);
        }
    }

    /// <summary>Ends the blink on controller <paramref name="controllerId"/>, or on every controller.</summary>
    /// <returns>False when there is no such controller.</returns>
    public bool StopIdentify(string? controllerId = null) => _devices.StopIdentify(controllerId);

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
        lock (_applyLock)
        {
            _zones.Dispose();
            _zones = ZoneLayer.Empty;
        }

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

    private static ControllerSetup SetupFrom(ISettingsStore settings, string? profileId = null) =>
        ControllerSetup.FromSettings(
            settings.GetSection("layout"),
            settings.GetSection("leds"),
            settings.GetSection("device"),
            settings.Controllers is [var first, ..] ? first.Name : "LED controller",
            profileId);

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
        if (_started && !_disposed)
        {
            ApplyZones();
        }
    }

    // The setup's zones replace the ones playing; an effect whose zone stays keeps running.
    private void ApplyZones()
    {
        var zones = ZoneLayer.Create(_setup.Zones, _zones, CreateZoneEffect, OnUnknownZoneEffect, out var retired);
        _zones = zones;
        _pipeline.ConfigureZones(zones);
        foreach (var effect in retired)
        {
            effect.Dispose();
        }

        if (zones.Count > 0)
        {
            var roles = string.Join(", ", zones.Roles.Select(r => r.Effect ?? $"[{r.R}, {r.G}, {r.B}]"));
            LogZones(zones.Count, roles);
        }
    }

    private EffectSource CreateZoneEffect(EffectDefinition definition) =>
        new(definition, priority: -1, durationMs: -1, _clock, MsvcRandom.CreateSeeded(), Sound);

    private void OnUnknownZoneEffect(string name)
    {
        if (_unknownZoneEffects.Add(name))
        {
            LogUnknownZoneEffect(name);
        }
    }

    // Compiles the settings for the current profile and applies the result; true when the LEDs' rects changed.
    private bool Recompile()
    {
        var previous = _setup.AllLeds();
        _setup = SetupFrom(_settings, _profileId);
        var allLeds = _setup.AllLeds();
        var changed = !JsonNode.DeepEquals(previous, allLeds);
        if (changed)
        {
            Layout = LedLayout.FromJson(allLeds);
        }

        ApplySetup();
        UpdateOutput();
        return changed;
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

            if (!on && component is Component.All or Component.LedDevice)
            {
                // the user wants the lights off: that includes a blink
                _devices.StopIdentify(null);
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
                if (held)
                {
                    // another app drives the LEDs (or the first run isn't completed): nothing may be sent, blinks neither
                    _devices.StopIdentify(null);
                }

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

    // The controllers run while the output is on and something is visible or zones play ("no source left": HyperHDR
    // switched the LED device off; here the LEDDEVICE switch keeps the user's choice). With zones and no source, the
    // other LEDs are black. While capture is on, the screen may only have stopped delivering for a moment (a display
    // change, a restarting capture): the controllers then stay connected for NoSourceOffDelay, showing black.
    private void UpdateOutput()
    {
        var on = Components.OutputActive && (Sources.Visible.HasSource || _setup.Zones.Count > 0);
        _devices.SetEnabled(on, Components.OutputActive && _screenRegistered ? NoSourceOffDelay : TimeSpan.Zero);
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
                Recompile();
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Zones: {Count} ({Roles})")]
    private partial void LogZones(int count, string roles);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A zone plays the effect '{Name}', which doesn't exist; it stays dark")]
    private partial void LogUnknownZoneEffect(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Profile: {Profile}")]
    private partial void LogProfile(string profile);
}
