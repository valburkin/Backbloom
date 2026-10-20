using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Engine;
using Overglow.Settings;

namespace Overglow.Shell;

/// <summary>
/// Switches the lighting for power and session events (port of <c>InstanceManager::hibernate</c> and
/// <c>ComponentController::turnGrabbers</c>):
/// <list type="bullet">
/// <item>sleep / hibernate: <c>ALL</c> off; 3 s after waking up (as in C++) on again;</item>
/// <item>session locked or display off, while <c>general.disableOnLocked</c> is on: the screen capture
/// (<c>SYSTEMGRABBER</c>) pauses, so the LEDs go dark unless a color or effect is shown; 3 s after the session is
/// unlocked and the display is on again it resumes.</item>
/// </list>
/// Differences from C++: only what this policy switched off is switched on again (C++ switched <c>ALL</c> on after
/// every wake-up, even when the user had switched it off before), a switch by the user in between wins, lock and
/// display-off are tracked separately (C++ resumed capture on unlock while the display was still off), and
/// <c>disableOnLocked</c> applies without a restart.
/// </summary>
public sealed partial class PowerPolicy : IDisposable
{
    /// <summary>Delay before the lighting comes back after waking up or unlocking (C++: 3000 ms).</summary>
    public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(3);

    private readonly ISystemEvents _events;
    private readonly ComponentStates _components;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    // Guards the fields below. Components are switched outside of it: their Changed handlers take the engine's lock,
    // which is also held while the engine switches components (lock order).
    private readonly Lock _lock = new();
    private bool _allSwitchedOff;
    private bool _captureSwitchedOff;
    private bool _locked;
    private bool _displayOff;
    private ITimer? _resumeAll;
    private ITimer? _resumeCapture;
    private bool _disposed;
    private int _applyingThread;

    public PowerPolicy(ISystemEvents events, ComponentStates components, ISettingsStore settings, TimeProvider time, ILogger<PowerPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _events = events;
        _components = components;
        _settings = settings;
        _time = time;
        _logger = logger;
        _events.Raised += OnEvent;
        _components.Changed += OnComponentChanged;
    }

    /// <summary><c>general.disableOnLocked</c> (C++ default: on).</summary>
    private bool PauseWhileLocked =>
        _settings.GetSection("general") is not JsonObject general || general["disableOnLocked"] is not JsonValue v || !v.TryGetValue(out bool on) || on;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _resumeAll?.Dispose();
            _resumeCapture?.Dispose();
        }

        _events.Raised -= OnEvent;
        _components.Changed -= OnComponentChanged;
    }

    private void OnEvent(SystemEvent e)
    {
        Component? switchOff = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            switch (e)
            {
                case SystemEvent.Suspend:
                    LogSuspend();
                    Cancel(ref _resumeAll);
                    if (_components.IsOn(Component.All))
                    {
                        _allSwitchedOff = true;
                        switchOff = Component.All;
                    }

                    break;
                case SystemEvent.Resume:
                    Schedule(ref _resumeAll, ResumeAll, _allSwitchedOff);
                    break;
                case SystemEvent.SessionLocked:
                case SystemEvent.DisplayOff:
                    _locked |= e == SystemEvent.SessionLocked;
                    _displayOff |= e == SystemEvent.DisplayOff;
                    Cancel(ref _resumeCapture);
                    if (PauseWhileLocked && _components.IsOn(Component.SystemGrabber))
                    {
                        LogPauseCapture(e == SystemEvent.SessionLocked ? "the session is locked" : "the display is off");
                        _captureSwitchedOff = true;
                        switchOff = Component.SystemGrabber;
                    }

                    break;
                case SystemEvent.SessionUnlocked:
                case SystemEvent.DisplayOn:
                    _locked &= e != SystemEvent.SessionUnlocked;
                    _displayOff &= e != SystemEvent.DisplayOn;
                    Schedule(ref _resumeCapture, ResumeCapture, _captureSwitchedOff && !_locked && !_displayOff);
                    break;
            }
        }

        if (switchOff is { } component)
        {
            Apply(component, false);
        }
    }

    private static void Cancel(ref ITimer? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    private void Schedule(ref ITimer? timer, Action action, bool needed)
    {
        timer?.Dispose();
        timer = needed ? _time.CreateTimer(_ => action(), null, ResumeDelay, Timeout.InfiniteTimeSpan) : null;
    }

    private void ResumeAll()
    {
        lock (_lock)
        {
            if (_disposed || !_allSwitchedOff)
            {
                return;
            }

            _allSwitchedOff = false;
        }

        LogResume();
        Apply(Component.All, true);
    }

    private void ResumeCapture()
    {
        lock (_lock)
        {
            if (_disposed || !_captureSwitchedOff || _locked || _displayOff)
            {
                return;
            }

            _captureSwitchedOff = false;
        }

        LogResumeCapture();
        Apply(Component.SystemGrabber, true);
    }

    private void Apply(Component component, bool on)
    {
        Volatile.Write(ref _applyingThread, Environment.CurrentManagedThreadId);
        try
        {
            _components.Set(component, on);
        }
        finally
        {
            Volatile.Write(ref _applyingThread, 0);
        }
    }

    // A switch by someone else while the policy holds a component off: that choice stays.
    private void OnComponentChanged(Component component, bool on)
    {
        if (Volatile.Read(ref _applyingThread) == Environment.CurrentManagedThreadId)
        {
            return;
        }

        lock (_lock)
        {
            if (component == Component.All)
            {
                _allSwitchedOff = false;
            }
            else if (component == Component.SystemGrabber)
            {
                _captureSwitchedOff = false;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The system is going to sleep: lights off")]
    private partial void LogSuspend();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The system woke up: lights on")]
    private partial void LogResume();

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen capture paused: {Reason}")]
    private partial void LogPauseCapture(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen capture resumed")]
    private partial void LogResumeCapture();
}
