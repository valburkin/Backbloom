using Overglow.Engine.Platform;

namespace Overglow.Engine;

/// <summary>
/// Whether the screen source delivers: it registers inactive, becomes active with its first frame and inactive again
/// after <see cref="InactiveAfterMs"/> without one, so a source below it (the background effect, or nothing) shows
/// while capture is down (lock screen, monitor off, capture errors). Port of the <c>_sysInactiveTimer</c> rule of
/// SystemControl (sources/base/SystemControl.cpp). The frame loop reports frames; while the screen is inactive it
/// probes the frame source now and then (<see cref="ProbeDue"/>), since it doesn't capture for a hidden screen.
/// </summary>
/// <remarks>
/// State changes are decided under a lock and applied to <see cref="PriorityManager"/> outside it (its events take the
/// engine's lock). A late call for a priority that was cleared meanwhile is ignored by the priority manager.
/// </remarks>
public sealed class ScreenActivity(PriorityManager sources, IClock clock)
{
    /// <summary>Time without frames after which the screen counts as inactive (HyperHDR: 800 ms).</summary>
    public const long InactiveAfterMs = 800;

    /// <summary>How often an inactive screen is probed.</summary>
    public const long ProbeIntervalMs = 500;

    private readonly Lock _lock = new();
    private int _priority = -1;
    private bool _active;
    private long _lastFrame;
    private long _lastProbe;

    /// <summary>The screen is registered but delivers nothing yet: probe it when <see cref="ProbeDue"/>.</summary>
    public bool NeedsProbe
    {
        get
        {
            lock (_lock)
            {
                return _priority >= 0 && !_active;
            }
        }
    }

    /// <summary>A probe is wanted now (at most every <see cref="ProbeIntervalMs"/>).</summary>
    public bool ProbeDue
    {
        get
        {
            lock (_lock)
            {
                return _priority >= 0 && !_active && clock.NowMilliseconds - _lastProbe >= ProbeIntervalMs;
            }
        }
    }

    public bool Active
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    /// <summary>The engine registered the screen at <paramref name="priority"/> (inactive).</summary>
    public void Registered(int priority)
    {
        lock (_lock)
        {
            _priority = priority;
            _active = false;
            _lastProbe = long.MinValue / 2;
        }
    }

    /// <summary>The engine removed the screen source.</summary>
    public void Unregistered()
    {
        lock (_lock)
        {
            _priority = -1;
            _active = false;
        }
    }

    /// <summary>A probe starts (counts toward <see cref="ProbeIntervalMs"/>).</summary>
    public void Probing()
    {
        lock (_lock)
        {
            _lastProbe = clock.NowMilliseconds;
        }
    }

    /// <summary>The frame source delivered a frame.</summary>
    public void FrameCaptured()
    {
        int activate;
        lock (_lock)
        {
            _lastFrame = clock.NowMilliseconds;
            activate = !_active && _priority >= 0 ? _priority : -1;
            _active |= activate >= 0;
        }

        if (activate >= 0)
        {
            sources.SetActive(activate);
        }
    }

    /// <summary>The frame source delivered nothing this time.</summary>
    public void NoFrame()
    {
        int deactivate;
        lock (_lock)
        {
            deactivate = _active && _priority >= 0 && clock.NowMilliseconds - _lastFrame >= InactiveAfterMs ? _priority : -1;
            _active &= deactivate < 0;
        }

        if (deactivate >= 0)
        {
            sources.SetInactive(deactivate);
        }
    }
}
