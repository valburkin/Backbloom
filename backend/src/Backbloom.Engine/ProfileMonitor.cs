using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Backbloom.Engine.Platform;
using Backbloom.Settings;
using Backbloom.Settings.Layout;

namespace Backbloom.Engine;

/// <summary>The profile whose roles (and Look) the strips have now.</summary>
/// <param name="Manual">Chosen by hand (<see cref="ProfileMonitor.Select"/>) rather than by a running program.</param>
public sealed record ActiveProfile(string Id, string Name, bool Manual);

/// <summary>A profile turned off with <see cref="ProfileMonitor.Dismiss"/> while its programs still run.</summary>
public sealed record DismissedProfile(string Id, string Name);

/// <summary>The profile that is on, if any, and the one that is off until its programs quit, if any.</summary>
public sealed record ProfileState(ActiveProfile? Profile, DismissedProfile? Dismissed)
{
    public static readonly ProfileState None = new(null, null);
}

/// <summary>
/// Picks the layout's profile (<c>layout.profiles</c>, the UI's presets) that is on: the one chosen by hand
/// (<see cref="Select"/>, saved as <c>presets.selected</c> and restored at start) until it is dismissed, another is
/// chosen or it is removed; else the first one in list order one of whose programs runs, except a dismissed one
/// (<see cref="Dismiss"/>, not saved), which stays off until none of its programs runs any more or the program restarts.
/// Has the engine run its setup and Look (<see cref="LightingEngine.ApplyProfile"/>: the layout isn't changed, the
/// controllers stay connected) and, when a profile is turned on, clears the colors and effects clients started
/// (<see cref="LightingEngine.ClearClientSources"/>) so that its own strips show. Polls the process list like the
/// <see cref="OtherAppMonitor"/>, every <see cref="PollInterval"/>, only while the layout has profiles and the programs
/// matter (none was chosen by hand, or one is dismissed).
/// </summary>
/// <remarks>
/// Lock order: this monitor's lock, then the engine's (and the capture rate follower's and the store's below it). The
/// choice is saved without the monitor's lock (the store raises its events on the saving thread); a separate save lock,
/// taken before the monitor's and never under it, keeps the saves in order, each one saving the latest choice. The monitor follows <c>presets</c> written by others (a
/// restored backup, <c>setconfig</c>) and ignores the events of its own saves.
/// </remarks>
public sealed partial class ProfileMonitor : IDisposable
{
    /// <summary>The settings section that holds the profile chosen by hand: <c>{ selected?: string }</c>.</summary>
    public const string SectionName = "presets";

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly LightingEngine _engine;
    private readonly ISettingsStore _settings;
    private readonly IProcessList _processes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private IReadOnlyList<LayoutProfile> _profiles = [];
    private ITimer? _timer;
    private string? _manualId;
    private ProfileState _state = ProfileState.None;
    private int _savingThread;
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

    /// <summary>Raised after <see cref="State"/> changed (on the thread that changed it).</summary>
    public event Action<ProfileState>? Changed;

    /// <summary>The profile that is on now, or null (the layout as saved).</summary>
    public ActiveProfile? Current => State.Profile;

    /// <summary>The profile that is on now and the dismissed one.</summary>
    public ProfileState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Reads the profiles and the one chosen by hand before (<c>presets.selected</c>, cleared when the layout no longer
    /// has it), applies the one that is on, then follows the programs and the settings.
    /// </summary>
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
            _manualId = Known(SavedId(_settings.GetSection(SectionName)));
        }

        _settings.Changed += OnSettingsChanged;
        Check();
        Save();
    }

    /// <summary>
    /// Runs the profile <paramref name="id"/> whatever programs run (also a dismissed one), until <see cref="Dismiss"/>,
    /// another call, or a call with null (back to choosing by the running programs; a dismissal stays). The choice is
    /// saved (<c>presets.selected</c>), so it survives a restart.
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
            if (id is not null)
            {
                _dismissed.Remove(id);
            }
        }

        Check();
        Save();
        return true;
    }

    /// <summary>
    /// Turns the profile that is on off: one chosen by hand goes back to choosing by the programs (and is no longer
    /// saved), and like one turned on by its program it stays off while any of its programs runs (the next run after
    /// they all quit turns it on again). Another profile whose program runs may be turned on instead. The dismissal
    /// itself isn't saved: a restart ends it.
    /// </summary>
    public ProfileState Dismiss()
    {
        lock (_lock)
        {
            if (_state.Profile is { } current)
            {
                if (current.Manual)
                {
                    _manualId = null;
                }

                // the check drops it again at once when none of its programs runs
                _dismissed.Add(current.Id);
            }
        }

        Check();
        Save();
        return State;
    }

    /// <summary>Decides now which profile is on (also called by the timer).</summary>
    public void Check()
    {
        ProfileState previous;
        ProfileState next;
        lock (_lock)
        {
            if (_disposed || !_started)
            {
                return;
            }

            LayoutProfile? running = null;
            var read = (_manualId is null || _dismissed.Count > 0) && TryReadPrograms(out running);
            UpdateTimer();
            ActiveProfile? profile;
            if (_manualId is not null)
            {
                profile = _profiles.FirstOrDefault(p => p.Id == _manualId) is { } chosen ? new ActiveProfile(chosen.Id, chosen.Name, Manual: true) : null;
            }
            else if (read)
            {
                profile = running is null ? null : new ActiveProfile(running.Id, running.Name, Manual: false);
            }
            else
            {
                // the process list can't be read: a profile turned on by its program stays on (unless it was dismissed)
                profile = _state.Profile is { Manual: false } kept && !_dismissed.Contains(kept.Id) && _profiles.FirstOrDefault(p => p.Id == kept.Id) is { } same
                    ? new ActiveProfile(same.Id, same.Name, Manual: false)
                    : null;
            }

            var dismissed = _profiles.FirstOrDefault(p => _dismissed.Contains(p.Id)) is { } off ? new DismissedProfile(off.Id, off.Name) : null;
            next = new ProfileState(profile, dismissed);
            previous = _state;
            if (next == previous)
            {
                return;
            }

            // under the lock, so that two checks can't apply their profiles in the wrong order
            _engine.ApplyProfile(profile?.Id);
            if (profile is not null && profile.Id != previous.Profile?.Id)
            {
                // last choice wins: a profile that is turned on shows its own strips, not a color or effect started before
                _engine.ClearClientSources();
            }

            _state = next;
        }

        if (next.Profile != previous.Profile)
        {
            LogActive(next.Profile?.Name ?? "(none)", next.Profile?.Manual ?? false);
        }

        if (next.Dismissed != previous.Dismissed)
        {
            LogDismissed(next.Dismissed?.Name ?? "(none)");
        }

        // the state now: a check that applied another one meanwhile may have raised its event first
        Changed?.Invoke(State);
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

    // Drops the dismissals whose programs all quit and finds the first profile (in list order, not dismissed) one of
    // whose programs runs. Runs under _lock; false when the process list can't be read (everything stays).
    private bool TryReadPrograms(out LayoutProfile? running)
    {
        running = null;
        try
        {
            foreach (var profile in _profiles)
            {
                if (_dismissed.Contains(profile.Id))
                {
                    if (profile.Apps.Count == 0 || _processes.FindRunning(profile.Apps) is null)
                    {
                        // none of its programs runs any more: the next run turns it on again
                        _dismissed.Remove(profile.Id);
                    }
                }
                else if (running is null && _manualId is null && profile.Apps.Count > 0 && _processes.FindRunning(profile.Apps) is not null)
                {
                    running = profile;
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

    // Polls only while there are profiles and the programs matter. Runs under _lock.
    private void UpdateTimer()
    {
        var wanted = _profiles.Count > 0 && (_manualId is null || _dismissed.Count > 0);
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
        var layout = change.Contains("layout");

        // a choice saved by someone else (a restored backup, setconfig); this monitor's own saves change nothing
        var presets = change.Sections.TryGetValue(SectionName, out var saved) && Volatile.Read(ref _savingThread) != Environment.CurrentManagedThreadId;
        if (!layout && !presets)
        {
            return;
        }

        lock (_lock)
        {
            if (layout)
            {
                _profiles = LayoutSpecification.Profiles(_settings.GetSection("layout") as JsonObject);
                _dismissed.RemoveWhere(id => _profiles.All(p => p.Id != id));
            }

            if (presets)
            {
                _manualId = SavedId(saved);
                if (_manualId is not null)
                {
                    _dismissed.Remove(_manualId);
                }
            }

            // a chosen profile the layout doesn't have (any more): back to choosing by the programs
            _manualId = Known(_manualId);
        }

        Check();
        Save();
    }

    // Saves the profile chosen by hand as presets.selected (none: without it) when the store has another one. Called
    // without _lock: the store raises its events on this thread, and handlers may take the engine's lock. _saveLock keeps
    // the saves in order, each one saving the choice as it is then.
    private void Save()
    {
        lock (_saveLock)
        {
            string? id;
            lock (_lock)
            {
                if (_disposed || !_started)
                {
                    return;
                }

                id = _manualId;
            }

            var section = _settings.GetSection(SectionName) as JsonObject ?? [];
            if (SavedId(section) == id)
            {
                return;
            }

            if (id is null)
            {
                section.Remove("selected");
            }
            else
            {
                section["selected"] = id;
            }

            Volatile.Write(ref _savingThread, Environment.CurrentManagedThreadId);
            try
            {
                var result = _settings.Update(new JsonObject { [SectionName] = section });
                if (!result.Success)
                {
                    LogSaveFailed(string.Join("; ", result.Errors));
                }
            }
            finally
            {
                Volatile.Write(ref _savingThread, 0);
            }
        }
    }

    // The id when the layout has a profile with it, else null. Runs under _lock.
    private string? Known(string? id) => id is not null && _profiles.Any(p => p.Id == id) ? id : null;

    private static string? SavedId(JsonNode? section) =>
        section?["selected"] is JsonValue v && v.TryGetValue(out string? id) ? id : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Active profile: {Profile} (chosen by hand: {Manual})")]
    private partial void LogActive(string profile, bool manual);

    [LoggerMessage(Level = LogLevel.Information, Message = "Profile off until its programs quit: {Profile}")]
    private partial void LogDismissed(string profile);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the process list to choose a profile: {Reason}")]
    private partial void LogCheckFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save the profile chosen by hand: {Reason}")]
    private partial void LogSaveFailed(string reason);
}
