using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Settings;

namespace Backbloom.Engine;

/// <summary>The mode the lights are in.</summary>
/// <param name="Saved">The mode chosen in the app (<c>modes.mode</c>).</param>
/// <param name="GameProfile">The name of the game's profile that switched Game mode on; null when <see cref="Saved"/> applies.</param>
/// <param name="DismissedGame">
/// The name of the running game's profile whose Game mode was turned off by choosing Ambient (until it quits); else null.
/// </param>
public sealed record ModeState(LightMode Mode, LightMode Saved, string? GameProfile, string? DismissedGame = null);

/// <summary>
/// Decides between Ambient and Game mode and has the engine apply it (<see cref="LightingEngine.ApplyGameMode"/>): the
/// saved mode (<c>modes.mode</c>), except that a profile marked as a game (<see cref="Settings.Layout.LayoutProfile.Game"/>)
/// turns Game mode on while it runs. Choosing Ambient while such a profile runs (<see cref="Set"/>) keeps Ambient until
/// another profile runs; choosing Game again then turns it back on without changing the saved mode.
/// </summary>
public sealed partial class ModeMonitor : IDisposable
{
    private readonly LightingEngine _engine;
    private readonly ISettingsStore _settings;
    private readonly ProfileMonitor _profiles;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private ModeState _current = new(LightMode.Ambient, LightMode.Ambient, null);
    private string? _dismissedProfileId;
    private bool _started;
    private bool _disposed;

    public ModeMonitor(LightingEngine engine, ISettingsStore settings, ProfileMonitor profiles, ILogger<ModeMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(logger);
        _engine = engine;
        _settings = settings;
        _profiles = profiles;
        _logger = logger;
    }

    /// <summary>
    /// Raised after <see cref="Current"/> changed, on the thread that changed it and under the monitor's lock, so that
    /// handlers see the changes in order. Handlers must be quick and must not call <see cref="Set"/>.
    /// </summary>
    public event Action<ModeState>? Changed;

    public ModeState Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Applies the mode that applies now, then follows the profiles and the settings.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _profiles.Changed += OnProfileChanged;
        _settings.Changed += OnSettingsChanged;
        Check();
    }

    /// <summary>
    /// Saves <paramref name="mode"/> as the chosen mode. Ambient while a game's profile has Game mode on also turns
    /// Game mode off until another profile runs; Game while that game still runs only turns it back on (the saved mode
    /// stays).
    /// </summary>
    public SettingsUpdateResult Set(LightMode mode)
    {
        var running = _profiles.Current is { Game: true } game ? game : null;
        string? previous;
        lock (_lock)
        {
            previous = _dismissedProfileId;
            if (mode == LightMode.Game && running is not null && _dismissedProfileId == running.Id)
            {
                _dismissedProfileId = null;
                Check();
                return new SettingsUpdateResult([], []);
            }

            // before saving, so that the saved Ambient doesn't show Game for the running game in between
            _dismissedProfileId = mode == LightMode.Ambient ? running?.Id : null;
        }

        var section = _settings.GetSection(GameTuning.ModesSectionName) as JsonObject ?? [];
        section["mode"] = GameTuning.ToSettingString(mode);
        var result = _settings.Update(new JsonObject { [GameTuning.ModesSectionName] = section });
        if (!result.Success)
        {
            lock (_lock)
            {
                _dismissedProfileId = previous;
            }
        }

        // also when the saved mode didn't change (no settings event), for the dismissal
        Check();
        return result;
    }

    /// <summary>Decides now which mode applies.</summary>
    public void Check()
    {
        ModeState next;
        lock (_lock)
        {
            if (_disposed || !_started)
            {
                return;
            }

            var saved = GameTuning.SavedMode(_settings.GetSection(GameTuning.ModesSectionName));
            var profile = _profiles.Current;
            if (_dismissedProfileId is not null && profile?.Id != _dismissedProfileId)
            {
                _dismissedProfileId = null;
            }

            next = profile is { Game: true } && profile.Id != _dismissedProfileId
                ? new ModeState(LightMode.Game, saved, profile.Name)
                : new ModeState(saved, saved, null, profile is { Game: true } && profile.Id == _dismissedProfileId ? profile.Name : null);
            if (next == _current)
            {
                return;
            }

            // under the lock, so that two checks can't apply or announce their modes in the wrong order
            _engine.ApplyGameMode(next.Mode == LightMode.Game);
            _current = next;
            LogMode(next.Mode, next.GameProfile ?? "(none)");
            Changed?.Invoke(next);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _profiles.Changed -= OnProfileChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    private void OnProfileChanged(ActiveProfile? profile) => Check();

    private void OnSettingsChanged(SettingsChange change)
    {
        if (change.Contains(GameTuning.ModesSectionName))
        {
            Check();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Mode: {Mode} (game profile: {Profile})")]
    private partial void LogMode(LightMode mode, string profile);
}
