using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Engine.Platform;
using Backbloom.Settings;
using Backbloom.Settings.Layout;

namespace Backbloom.Engine;

/// <summary>The profile whose roles the strips have now.</summary>
/// <param name="Manual">Chosen by hand (<see cref="ProfileMonitor.Select"/>) rather than by a running program.</param>
/// <param name="Game">The profile is a game's: Game mode is on while it runs (<see cref="ModeMonitor"/>).</param>
public sealed record ActiveProfile(string Id, string Name, bool Manual, bool Game = false);

/// <summary>
/// Picks the layout's profile (<c>layout.profiles</c>) that runs: the first one in list order one of whose programs
/// runs, or the one chosen by hand until <see cref="Select"/> is called with null again, and has the engine run its
/// setup (<see cref="LightingEngine.ApplyProfile"/>: nothing is saved, the controllers stay connected). Polls the
/// process list like the <see cref="OtherAppMonitor"/>, every <see cref="PollInterval"/>, only while the layout has
/// profiles and none was chosen by hand.
/// </summary>
public sealed partial class ProfileMonitor : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly LightingEngine _engine;
    private readonly ISettingsStore _settings;
    private readonly IProcessList _processes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private IReadOnlyList<LayoutProfile> _profiles = [];
    private ITimer? _timer;
    private string? _manualId;
    private ActiveProfile? _current;
    private bool _started;
    private bool _failed;
    private bool _disposed;

    public ProfileMonitor(LightingEngine engine, ISettingsStore settings, IProcessList processes, TimeProvider time, ILogger<ProfileMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _engine = engine;
        _settings = settings;
        _processes = processes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised after <see cref="Current"/> changed (on the thread that changed it).</summary>
    public event Action<ActiveProfile?>? Changed;

    /// <summary>The profile that runs now, or null (the layout as saved).</summary>
    public ActiveProfile? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Reads the profiles, applies the one that runs, then follows the programs and the settings.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _profiles = LayoutSpecification.Profiles(_settings.GetSection("layout") as JsonObject);
        }

        _settings.Changed += OnSettingsChanged;
        Check();
    }

    /// <summary>
    /// Runs the profile <paramref name="id"/> whatever programs run, until called with null (back to choosing by the
    /// running programs). Not saved: a restart chooses by the programs again.
    /// </summary>
    /// <returns>False when the layout has no profile with that id.</returns>
    public bool Select(string? id)
    {
        lock (_lock)
        {
            if (id is not null && _profiles.All(p => p.Id != id))
            {
                return false;
            }

            _manualId = id;
        }

        Check();
        return true;
    }

    /// <summary>Decides now which profile runs (also called by the timer).</summary>
    public void Check()
    {
        ActiveProfile? next;
        lock (_lock)
        {
            if (_disposed || !_started)
            {
                return;
            }

            UpdateTimer();
            if (_manualId is not null)
            {
                next = _profiles.FirstOrDefault(p => p.Id == _manualId) is { } chosen ? new ActiveProfile(chosen.Id, chosen.Name, Manual: true, chosen.Game) : null;
            }
            else if (!TryFindRunning(out var running))
            {
                return;
            }
            else
            {
                next = running is null ? null : new ActiveProfile(running.Id, running.Name, Manual: false, running.Game);
            }

            if (next == _current)
            {
                return;
            }

            // under the lock, so that two checks can't apply their profiles in the wrong order
            _engine.ApplyProfile(next?.Id);
            _current = next;
        }

        LogActive(next?.Name ?? "(none)", next?.Manual ?? false);

        // the profile that runs now: a check that applied another one meanwhile may have raised its event first
        Changed?.Invoke(Current);
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
            _timer?.Dispose();
            _timer = null;
        }

        _settings.Changed -= OnSettingsChanged;
    }

    // The first profile (in list order) one of whose programs runs. Runs under _lock; false when the process list
    // can't be read (the current profile stays).
    private bool TryFindRunning(out LayoutProfile? running)
    {
        running = null;
        try
        {
            foreach (var profile in _profiles)
            {
                if (profile.Apps.Count > 0 && _processes.FindRunning(profile.Apps) is not null)
                {
                    running = profile;
                    break;
                }
            }

            _failed = false;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_failed)
            {
                LogCheckFailed(ex.Message);
            }

            _failed = true;
            return false;
        }
    }

    // Polls only while there are profiles to choose from by the programs. Runs under _lock.
    private void UpdateTimer()
    {
        var wanted = _profiles.Count > 0 && _manualId is null;
        if (wanted && _timer is null)
        {
            _timer = _time.CreateTimer(_ => Check(), null, PollInterval, PollInterval);
        }
        else if (!wanted && _timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }

    private void OnSettingsChanged(SettingsChange change)
    {
        if (!change.Contains("layout"))
        {
            return;
        }

        lock (_lock)
        {
            _profiles = LayoutSpecification.Profiles(_settings.GetSection("layout") as JsonObject);
            if (_manualId is not null && _profiles.All(p => p.Id != _manualId))
            {
                // the chosen profile is gone: back to choosing by the programs
                _manualId = null;
            }
        }

        Check();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Active profile: {Profile} (chosen by hand: {Manual})")]
    private partial void LogActive(string profile, bool manual);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the process list to choose a profile: {Reason}")]
    private partial void LogCheckFailed(string reason);
}
