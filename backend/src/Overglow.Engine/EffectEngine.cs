using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Audio;
using Overglow.Core.Color;
using Overglow.Core.Effects;
using Overglow.Core.Priorities;
using Overglow.Core.Smoothing;
using Overglow.Engine.Platform;
using Overglow.Settings;

namespace Overglow.Engine;

/// <summary>An effect as <c>serverinfo.activeEffects</c> lists it.</summary>
/// <param name="TimeoutMs">The duration it was started with (-1: until cleared).</param>
public sealed record ActiveEffect(string Name, int Priority, long TimeoutMs);

/// <summary>
/// Runs effects as sources (<see cref="EffectSource"/> registered with <see cref="PriorityManager"/>), gives each
/// effect its smoothing configuration, and applies the <c>foregroundEffect</c> (startup), <c>backgroundEffect</c> and
/// <c>soundEffect</c> settings. Port of <c>EffectEngine</c> (sources/effects/EffectEngine.cpp) and the effect parts of
/// <c>LightingInstance::handleSettingsUpdate</c>. An effect ends when its source is cleared, replaced or expires.
/// Thread-safe; never calls the priority manager while holding its own lock (the manager's events take other locks).
/// </summary>
public sealed partial class EffectEngine : IDisposable
{
    /// <summary>Priority of the startup effect (HyperHDR: 0).</summary>
    public const int ForegroundPriority = Muxer.HighestEffectPriority;

    /// <summary>Priority of the background effect (HyperHDR: 254, kept by "clear all").</summary>
    public const int BackgroundPriority = Muxer.LowestEffectPriority;

    /// <summary>The startup effect runs at most this long after start when the LEDs were off (HyperHDR: 5 s).</summary>
    public const long ForegroundWindowMs = 5000;

    private const long DefaultForegroundDurationMs = 3000;
    private const string SystemOrigin = "System";

    private readonly PriorityManager _sources;
    private readonly ComponentStates _components;
    private readonly ISettingsStore _settings;
    private readonly IClock _clock;
    private readonly SoundCapture? _sound;
    private readonly ILogger _logger;
    private readonly Dictionary<string, int> _smoothingConfigs = new(StringComparer.Ordinal);
    private readonly Dictionary<int, EffectSource> _running = [];
    private readonly HashSet<EffectSource> _registered = [];
    private readonly Lock _lock = new();
    private long _startedAt = long.MinValue;
    private bool _foregroundDone;
    private bool _disposed;

    /// <param name="addSmoothingConfig">Registers a smoothing configuration (id, settling time, frequency, direct) and returns its id.</param>
    /// <param name="sound">Music mode's sound capture; null where there is none.</param>
    public EffectEngine(
        PriorityManager sources,
        ComponentStates components,
        ISettingsStore settings,
        Func<int, long, double, bool, int> addSmoothingConfig,
        IClock clock,
        SoundCapture? sound,
        ILogger<EffectEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(addSmoothingConfig);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _sources = sources;
        _components = components;
        _settings = settings;
        _clock = clock;
        _sound = sound;
        _logger = logger;

        // EffectEngine::createSmoothingConfigs: one default configuration for effects (200 ms, 25 Hz), and one per
        // effect that asks for its own (equal ones are shared)
        var defaultConfig = addSmoothingConfig(-1, SmoothingConfig.DefaultSettlingTimeMs, SmoothingConfig.DefaultUpdateFrequency, false);
        foreach (var effect in EffectCatalog.All)
        {
            _smoothingConfigs[effect.Name] = effect.HasCustomSmoothing
                ? addSmoothingConfig(-1, effect.SmoothingTimeMs, effect.SmoothingFrequency, effect.SmoothingDirect)
                : defaultConfig;
        }

        _sound?.Configure(SoundSettings.FromJson(settings.GetSection(SoundSettings.SectionName)));
        sources.PrioritiesChanged += Prune;
        components.Changed += OnComponentChanged;
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>Every effect that can be run.</summary>
    public static IReadOnlyList<EffectDefinition> Available => EffectCatalog.All;

    /// <summary>The running effects in priority order.</summary>
    public IReadOnlyList<ActiveEffect> Active
    {
        get
        {
            lock (_lock)
            {
                return [.. _running.Values.OrderBy(e => e.Priority).Select(e => new ActiveEffect(e.Definition.Name, e.Priority, e.DurationMs))];
            }
        }
    }

    /// <summary>Starts the background effect and the startup effect (when the LEDs are on).</summary>
    public void Start()
    {
        _startedAt = _clock.NowMilliseconds;
        ApplyBackground(_settings.GetSection("backgroundEffect"));
        if (_components.OutputActive)
        {
            RunForeground();
        }
    }

    /// <summary>
    /// Runs <paramref name="name"/> at <paramref name="priority"/> for <paramref name="durationMs"/> ms (&lt;= 0: until
    /// cleared), replacing what ran there (C++ <c>runEffect</c>).
    /// </summary>
    /// <returns>False when there is no such effect.</returns>
    public bool Run(string name, int priority, long durationMs, string origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var definition = EffectCatalog.Find(name);
        if (definition is null)
        {
            LogUnknown(name ?? string.Empty);
            return false;
        }

        var source = new EffectSource(definition, priority, durationMs, _clock, MsvcRandom.CreateSeeded(), _sound);
        EffectSource? replaced;
        lock (_lock)
        {
            if (_disposed)
            {
                source.Dispose();
                return false;
            }

            if (_running.Remove(priority, out replaced))
            {
                _registered.Remove(replaced);
            }

            _running[priority] = source;
        }

        replaced?.Dispose();
        _sources.Register(priority, SourceComponent.Effect, origin, definition.Name, source, _smoothingConfigs[definition.Name]);
        lock (_lock)
        {
            _registered.Add(source);
        }

        _sources.SetActive(priority, durationMs);
        LogStarted(definition.Name, priority, durationMs);
        return true;
    }

    public void Dispose()
    {
        EffectSource[] running;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = [.. _running.Values];
            _running.Clear();
            _registered.Clear();
        }

        _settings.Changed -= OnSettingsChanged;
        _components.Changed -= OnComponentChanged;
        _sources.PrioritiesChanged -= Prune;
        foreach (var source in running)
        {
            source.Dispose();
        }
    }

    // An effect whose source is gone (cleared, replaced by a color, expired) ends here.
    private void Prune()
    {
        List<EffectSource>? ended = null;
        lock (_lock)
        {
            foreach (var (priority, source) in _running)
            {
                if (_registered.Contains(source) && !ReferenceEquals(_sources.GetRenderer(priority), source))
                {
                    (ended ??= []).Add(source);
                }
            }

            foreach (var source in ended ?? [])
            {
                _running.Remove(source.Priority);
                _registered.Remove(source);
            }
        }

        foreach (var source in ended ?? [])
        {
            source.Dispose();
            LogEnded(source.Definition.Name, source.Priority);
        }
    }

    private void OnComponentChanged(Component component, bool on)
    {
        // HyperHDR started the boot effect when the LED device came on within 5 s of the start
        if (on && component is Component.LedDevice or Component.All && _components.OutputActive &&
            _startedAt != long.MinValue && _clock.NowMilliseconds - _startedAt < ForegroundWindowMs)
        {
            RunForeground();
        }
    }

    private void OnSettingsChanged(SettingsChange change)
    {
        if (change.Sections.TryGetValue(SoundSettings.SectionName, out var sound))
        {
            _sound?.Configure(SoundSettings.FromJson(sound));
        }

        if (change.Sections.TryGetValue("backgroundEffect", out var background))
        {
            ApplyBackground(background);
        }
    }

    private void RunForeground()
    {
        lock (_lock)
        {
            if (_foregroundDone)
            {
                return;
            }

            _foregroundDone = true;
        }

        var section = _settings.GetSection("foregroundEffect") as JsonObject;
        if (!Flag(section, "enable"))
        {
            return;
        }

        var duration = section?["duration_ms"] is JsonValue d && d.TryGetValue(out double ms) ? (long)ms : DefaultForegroundDurationMs;
        if (duration <= 0)
        {
            LogForegroundForever(DefaultForegroundDurationMs);
            duration = DefaultForegroundDurationMs;
        }

        RunConfigured(section, ForegroundPriority, duration, "Startup effect");
    }

    // LightingInstance::handleSettingsUpdate(BGEFFECT): the old background goes, the new one runs until cleared.
    private void ApplyBackground(JsonNode? section)
    {
        _sources.Clear(BackgroundPriority);
        if (Flag(section as JsonObject, "enable"))
        {
            RunConfigured(section as JsonObject, BackgroundPriority, Muxer.NoTimeout, "Background effect");
        }
    }

    private void RunConfigured(JsonObject? section, int priority, long durationMs, string what)
    {
        var type = section?["type"] is JsonValue t && t.TryGetValue(out string? s) ? s : "effect";
        if (type.Contains("color", StringComparison.Ordinal))
        {
            if (section?["color"] is not JsonArray { Count: >= 3 })
            {
                return;
            }

            var color = Rgb8.FromJson(section["color"], Rgb8.Black);
            _sources.SetColor(priority, color, durationMs, SystemOrigin);
            LogSystemColor(what, color.R, color.G, color.B);
        }
        else
        {
            var name = section?["effect"] is JsonValue e && e.TryGetValue(out string? n) ? n : "Rainbow swirl fast";
            var started = Run(name, priority, durationMs, SystemOrigin);
            LogSystemEffect(what, name, started ? "started" : "failed");
        }
    }

    // C++ reads the flags with toBool(true).
    private static bool Flag(JsonObject? o, string name) => o?[name] is not JsonValue v || !v.TryGetValue(out bool b) || b;

    [LoggerMessage(Level = LogLevel.Information, Message = "Effect '{Name}' runs at priority {Priority} (duration {Duration} ms)")]
    private partial void LogStarted(string name, int priority, long duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Effect '{Name}' at priority {Priority} has finished")]
    private partial void LogEnded(string name, int priority);

    [LoggerMessage(Level = LogLevel.Warning, Message = "There is no effect named '{Name}'")]
    private partial void LogUnknown(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A startup effect can't run forever; it runs {Duration} ms")]
    private partial void LogForegroundForever(long duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "{What}: color set to [{R}, {G}, {B}]")]
    private partial void LogSystemColor(string what, byte r, byte g, byte b);

    [LoggerMessage(Level = LogLevel.Information, Message = "{What}: animation '{Name}' {Result}")]
    private partial void LogSystemEffect(string what, string name, string result);
}
