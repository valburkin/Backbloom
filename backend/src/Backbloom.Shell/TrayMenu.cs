using System.Globalization;
using Microsoft.Extensions.Logging;
using Backbloom.Core.Color;
using Backbloom.Core.Effects;
using Backbloom.Core.Priorities;
using Backbloom.Engine;
using Backbloom.Engine.Platform;

namespace Backbloom.Shell;

/// <summary>
/// The tray icon's menu and what its entries do (the C++ SystrayHandler, reorganized): open the window, lights on/off,
/// screen sync, music mode, an effect, a static color (all at priority 1, as C++ did), pause for 30 minutes, start with
/// the OS, quit. The C++ "Instances" submenu is gone (one pipeline).
/// </summary>
public sealed partial class TrayMenu : IDisposable
{
    /// <summary>Priority of colors chosen in the tray (C++: 1).</summary>
    public const int ColorPriority = 1;

    /// <summary>How long "Pause" switches the lights off.</summary>
    public static readonly TimeSpan PauseDuration = TimeSpan.FromMinutes(30);

    /// <summary>The colors of the C++ tray menu.</summary>
    public static IReadOnlyList<(string Name, Rgb8 Color)> Colors { get; } =
    [
        ("White", new Rgb8(255, 255, 255)),
        ("Red", new Rgb8(255, 0, 0)),
        ("Green", new Rgb8(0, 255, 0)),
        ("Blue", new Rgb8(0, 0, 255)),
        ("Yellow", new Rgb8(255, 255, 0)),
        ("Magenta", new Rgb8(255, 0, 255)),
        ("Cyan", new Rgb8(0, 255, 255)),
    ];

    private readonly LightingEngine _engine;
    private readonly IAppWindow _window;
    private readonly IAutostart _autostart;
    private readonly Action _quit;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private ITimer? _pause;
    private DateTimeOffset _pausedUntil;
    private int _applyingThread;

    /// <param name="quit">Ends the application (stops the host).</param>
    public TrayMenu(LightingEngine engine, IAppWindow window, IAutostart autostart, Action quit, TimeProvider time, ILogger<TrayMenu> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(autostart);
        ArgumentNullException.ThrowIfNull(quit);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _engine = engine;
        _window = window;
        _autostart = autostart;
        _quit = quit;
        _time = time;
        _logger = logger;
        _engine.Components.Changed += OnComponentChanged;
    }

    /// <summary>When the running pause ends; null when not paused.</summary>
    public DateTimeOffset? PausedUntil
    {
        get
        {
            lock (_lock)
            {
                return _pause is null ? null : _pausedUntil;
            }
        }
    }

    public void OpenWindow() => _window.Show();

    /// <summary>The entries, reflecting the current state.</summary>
    public IReadOnlyList<TrayMenuItem> Build()
    {
        var lightsOn = _engine.Components.IsOn(Component.All);
        var visible = _engine.Sources.Visible;
        var effect = lightsOn && visible.Component == SourceComponent.Effect ? (visible.Renderer as EffectSource)?.Definition.Name : null;

        // the screen shows, or will with its first frame (it is a source only while it delivers)
        var screen = lightsOn && (visible.Component == SourceComponent.SystemGrabber || (!visible.HasSource && _engine.Components.IsOn(Component.SystemGrabber)));
        var colors = Colors.Select(c => new TrayMenuItem(
            c.Name,
            () => ShowColor(c.Color),
            Checked: lightsOn && visible.HasSource && visible.Component == SourceComponent.Color && visible.Priority == ColorPriority && visible.Color == c.Color)).ToList();
        TrayMenuItem EffectItem(EffectDefinition e) =>
            new(e.IsMusic ? e.Name[MusicEffect.NamePrefix.Length..] : e.Name, () => RunEffect(e.Name), Checked: e.Name == effect);
        var music = EffectEngine.Available.Where(e => e.IsMusic).Select(EffectItem).ToList();
        var effects = EffectEngine.Available.Where(e => !e.IsMusic).Select(EffectItem).ToList();
        var pausedUntil = PausedUntil;
        var pause = pausedUntil is { } until
            ? new TrayMenuItem($"Resume (paused until {until.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)})", Resume)
            : new TrayMenuItem("Pause for 30 minutes", Pause, Enabled: lightsOn);

        return
        [
            new TrayMenuItem("Open Backbloom", OpenWindow, IsDefault: true),
            TrayMenuItem.Separator,
            new TrayMenuItem("Lights", () => SetLights(!lightsOn), Checked: lightsOn),
            new TrayMenuItem("Screen sync", ShowScreen, Checked: screen),
            new TrayMenuItem("Music", Children: music, Checked: music.Any(m => m.Checked)),
            new TrayMenuItem("Effect", Children: effects, Checked: effects.Any(m => m.Checked)),
            new TrayMenuItem("Color", Children: colors, Checked: colors.Any(m => m.Checked)),
            pause,
            TrayMenuItem.Separator,
            new TrayMenuItem(_autostart.Label, ToggleAutostart, Checked: _autostart.IsEnabled),
            TrayMenuItem.Separator,
            new TrayMenuItem("Quit", _quit),
        ];
    }

    /// <summary>Switches the lights (<c>ALL</c>); ends a pause.</summary>
    public void SetLights(bool on)
    {
        CancelPause();
        _engine.Components.Set(Component.All, on);
    }

    /// <summary>The screen again: removes every color and effect, capture on.</summary>
    public void ShowScreen()
    {
        CancelPause();
        _engine.Clear(-1);
        _engine.Components.Set(Component.SystemGrabber, true);
        _engine.Components.Set(Component.All, true);
    }

    public void ShowColor(Rgb8 color)
    {
        CancelPause();
        _engine.SetColor(ColorPriority, color, 0, "System tray");
        _engine.Components.Set(Component.All, true);
    }

    /// <summary>Runs an effect (music mode is a "Music: " effect) until something else is chosen.</summary>
    public void RunEffect(string name)
    {
        CancelPause();
        if (_engine.RunEffect(name, ColorPriority, 0, "System tray"))
        {
            _engine.Components.Set(Component.All, true);
        }
    }

    /// <summary>Lights off for <see cref="PauseDuration"/>, then on again unless someone switched them meanwhile.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            _pause?.Dispose();
            _pausedUntil = _time.GetUtcNow() + PauseDuration;
            _pause = _time.CreateTimer(_ => EndPause(), null, PauseDuration, Timeout.InfiniteTimeSpan);
        }

        LogPaused(PauseDuration.TotalMinutes);
        Apply(false);
    }

    /// <summary>Ends a pause early: lights on.</summary>
    public void Resume()
    {
        CancelPause();
        Apply(true);
    }

    public void Dispose()
    {
        _engine.Components.Changed -= OnComponentChanged;
        CancelPause();
    }

    private void ToggleAutostart()
    {
        var enable = !_autostart.IsEnabled;
        try
        {
            _autostart.SetEnabled(enable);
            LogAutostart(enable);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            LogAutostartFailed(ex.Message);
        }
    }

    private void EndPause()
    {
        lock (_lock)
        {
            if (_pause is null)
            {
                return;
            }

            _pause.Dispose();
            _pause = null;
        }

        LogPauseEnded();
        Apply(true);
    }

    private void CancelPause()
    {
        lock (_lock)
        {
            _pause?.Dispose();
            _pause = null;
        }
    }

    private void Apply(bool on)
    {
        Volatile.Write(ref _applyingThread, Environment.CurrentManagedThreadId);
        try
        {
            _engine.Components.Set(Component.All, on);
        }
        finally
        {
            Volatile.Write(ref _applyingThread, 0);
        }
    }

    // Lights switched by the UI, the API or the power policy end a pause: their choice stays.
    private void OnComponentChanged(Component component, bool on)
    {
        if (component == Component.All && Volatile.Read(ref _applyingThread) != Environment.CurrentManagedThreadId)
        {
            CancelPause();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Lights paused for {Minutes} minutes")]
    private partial void LogPaused(double minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "The pause ended: lights on")]
    private partial void LogPauseEnded();

    [LoggerMessage(Level = LogLevel.Information, Message = "Start with the OS: {Enabled}")]
    private partial void LogAutostart(bool enabled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't change the autostart entry: {Reason}")]
    private partial void LogAutostartFailed(string reason);
}
