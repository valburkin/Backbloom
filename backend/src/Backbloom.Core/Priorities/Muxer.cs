using Backbloom.Core.Color;

namespace Backbloom.Core.Priorities;

/// <summary>The kinds of sources competing for the LEDs, with their protocol ids.</summary>
public enum SourceComponent
{
    /// <summary>"COLOR": a static color (also the "no source" entry at priority 255, black).</summary>
    Color,

    /// <summary>"IMAGE": a picture sent through the API.</summary>
    Image,

    /// <summary>"EFFECT": an effect, also music mode (effects and music come with port step 5b).</summary>
    Effect,

    /// <summary>"SYSTEMGRABBER": the screen.</summary>
    SystemGrabber,
}

public static class SourceComponents
{
    public static string ToId(this SourceComponent component) => component switch
    {
        SourceComponent.Color => "COLOR",
        SourceComponent.Image => "IMAGE",
        SourceComponent.Effect => "EFFECT",
        SourceComponent.SystemGrabber => "SYSTEMGRABBER",
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };
}

/// <summary>One registered source (C++ <c>Muxer::InputInfo</c>).</summary>
/// <param name="TimeoutMs">Absolute expiry time (ms), <see cref="Muxer.NoTimeout"/> or <see cref="Muxer.Inactive"/>.</param>
/// <param name="SmoothingConfig">Smoothing configuration id used while this source is visible.</param>
public sealed record SourceInput(
    int Priority,
    SourceComponent Component,
    string Origin,
    string? Owner,
    Rgb8 StaticColor,
    int SmoothingConfig,
    long TimeoutMs)
{
    /// <summary>The source delivers (C++: <c>timeout &gt;= -1</c>).</summary>
    public bool Active => TimeoutMs >= Muxer.NoTimeout;
}

/// <summary>What a muxer call changed, for the caller to announce after it released its lock.</summary>
[Flags]
public enum MuxerChanges
{
    None = 0,

    /// <summary>The list of sources or their state changed (<c>priorities-update</c>).</summary>
    Priorities = 1,

    /// <summary>Another source is visible now.</summary>
    Visible = 2,
}

/// <summary>
/// Picks the visible source: the lowest priority number among the active sources, or the one selected manually.
/// Sources can expire. Port of HyperHDR's <c>Muxer</c> (sources/base/Muxer.cpp, MIT, awawa-dev) without its timers:
/// the caller passes the time and calls <see cref="Update"/> periodically (HyperHDR: every 250 ms). Not thread-safe.
/// </summary>
public sealed class Muxer
{
    /// <summary>The "no source" entry: always present, black.</summary>
    public const int LowestPriority = 255;

    public const int HighestEffectPriority = 0;

    public const int LowestEffectPriority = 254;

    /// <summary>Timeout of a source that never expires.</summary>
    public const long NoTimeout = -1;

    /// <summary>Timeout of a registered source that doesn't deliver yet (C++ <c>TIMEOUT_INACTIVE</c>).</summary>
    public const long Inactive = -100;

    private static readonly SourceInput s_lowest = new(LowestPriority, SourceComponent.Color, "System", null, Rgb8.Black, 0, NoTimeout);

    private readonly SortedDictionary<int, SourceInput> _inputs = new() { [LowestPriority] = s_lowest };
    private int _manualPriority = LowestPriority;
    private SourceComponent? _visibleComponent;

    public int CurrentPriority { get; private set; } = LowestPriority;

    public int PreviousPriority { get; private set; } = LowestPriority;

    public bool AutoSelect { get; private set; } = true;

    /// <summary>
    /// Only the screen is shown (Game mode): colors, pictures and effects stay registered and keep their timeouts, but
    /// aren't selected until this is turned off again. Not in the C++ code.
    /// </summary>
    public bool ScreenOnly { get; private set; }

    /// <summary>Every source in priority order, including the "no source" entry.</summary>
    public IReadOnlyCollection<SourceInput> Inputs => _inputs.Values;

    /// <summary>The visible source.</summary>
    public SourceInput Visible => Get(CurrentPriority);

    /// <summary>The source at <paramref name="priority"/>, or the "no source" entry.</summary>
    public SourceInput Get(int priority) => _inputs.TryGetValue(priority, out var input) ? input : _inputs.GetValueOrDefault(LowestPriority, s_lowest);

    public bool Has(int priority) => priority == LowestPriority || _inputs.ContainsKey(priority);

    /// <summary>
    /// Registers a source, or updates the description of an existing one (its timeout stays). A new source is inactive
    /// until <see cref="SetInput"/>.
    /// </summary>
    public MuxerChanges Register(int priority, SourceComponent component, string origin, string? owner, Rgb8 staticColor, int smoothingConfig)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var isNew = !_inputs.TryGetValue(priority, out var existing);
        var reused = !isNew && (_visibleComponent == component || existing!.Component == component);
        _inputs[priority] = new SourceInput(priority, component, origin, string.IsNullOrEmpty(owner) ? null : owner, staticColor, smoothingConfig, isNew ? Inactive : existing!.TimeoutMs);
        if (isNew)
        {
            return AutoSelect ? MuxerChanges.None : MuxerChanges.Priorities;
        }

        return reused ? MuxerChanges.Priorities : MuxerChanges.None;
    }

    /// <summary>
    /// The source at <paramref name="priority"/> delivers: active for <paramref name="timeoutMs"/> ms from
    /// <paramref name="nowMs"/> (&lt;= 0: no timeout; <see cref="Inactive"/>: stops delivering).
    /// </summary>
    /// <returns>Null when the priority isn't registered.</returns>
    public MuxerChanges? SetInput(int priority, long timeoutMs, long nowMs)
    {
        if (!_inputs.TryGetValue(priority, out var input))
        {
            return null;
        }

        if (timeoutMs > 0)
        {
            timeoutMs = nowMs + timeoutMs;
        }

        var activeChange = (input.TimeoutMs == Inactive) != (timeoutMs == Inactive);
        _inputs[priority] = input with { TimeoutMs = timeoutMs };
        if (!activeChange)
        {
            return MuxerChanges.None;
        }

        var changes = CurrentPriority < priority ? MuxerChanges.Priorities : MuxerChanges.None;
        return changes | Update(nowMs);
    }

    /// <summary>The source stops delivering but stays registered.</summary>
    public MuxerChanges? SetInactive(int priority, long nowMs) => SetInput(priority, Inactive, nowMs);

    /// <summary>Removes a source (the "no source" entry can't be removed).</summary>
    /// <returns>Null when the priority can't be cleared.</returns>
    public MuxerChanges? Clear(int priority, long nowMs)
    {
        if (priority >= LowestPriority)
        {
            return null;
        }

        var changes = MuxerChanges.None;
        if (_inputs.Remove(priority))
        {
            changes |= Update(nowMs);
        }

        if (!AutoSelect || CurrentPriority < priority)
        {
            changes |= MuxerChanges.Priorities;
        }

        return changes;
    }

    /// <summary>
    /// Removes the colors, pictures and effects below priority 253 (the API's "clear all"), or with
    /// <paramref name="force"/> every source.
    /// </summary>
    public MuxerChanges ClearAll(long nowMs, bool force = false)
    {
        if (force)
        {
            PreviousPriority = CurrentPriority;
            _inputs.Clear();
            _inputs[LowestPriority] = s_lowest;
            CurrentPriority = LowestPriority;
            return MuxerChanges.Priorities | MuxerChanges.Visible;
        }

        var changes = MuxerChanges.None;
        foreach (var input in _inputs.Values.ToArray())
        {
            if ((input.Component is SourceComponent.Color or SourceComponent.Effect or SourceComponent.Image) && input.Priority < LowestPriority - 1)
            {
                changes |= Clear(input.Priority, nowMs) ?? MuxerChanges.None;
            }
        }

        return changes;
    }

    /// <summary>Switches between automatic selection and the manually selected priority.</summary>
    /// <returns>False when nothing changed (or a manual selection isn't possible any more).</returns>
    public bool SetAutoSelect(bool enabled, long nowMs, out MuxerChanges changes)
    {
        changes = MuxerChanges.None;
        if (AutoSelect == enabled)
        {
            return false;
        }

        if (!enabled && !_inputs.ContainsKey(_manualPriority))
        {
            return false;
        }

        AutoSelect = enabled;
        changes = Update(nowMs) | MuxerChanges.Priorities;
        return true;
    }

    /// <summary>Shows <paramref name="priority"/> regardless of the others (manual selection).</summary>
    public bool Select(int priority, long nowMs, out MuxerChanges changes)
    {
        changes = MuxerChanges.None;
        if (!_inputs.ContainsKey(priority))
        {
            return false;
        }

        _manualPriority = priority;
        if (!SetAutoSelect(false, nowMs, out changes))
        {
            changes = Update(nowMs) | MuxerChanges.Priorities;
        }

        return true;
    }

    /// <summary>Turns <see cref="ScreenOnly"/> on or off.</summary>
    public MuxerChanges SetScreenOnly(bool enabled, long nowMs)
    {
        if (ScreenOnly == enabled)
        {
            return MuxerChanges.None;
        }

        ScreenOnly = enabled;
        return Update(nowMs) | MuxerChanges.Priorities;
    }

    /// <summary>Whether <paramref name="input"/> waits while <see cref="ScreenOnly"/> is on.</summary>
    private bool Held(SourceInput input) =>
        ScreenOnly && input.Priority < LowestPriority && input.Component is SourceComponent.Color or SourceComponent.Effect or SourceComponent.Image;

    /// <summary>
    /// Drops expired sources and selects the visible one (C++ <c>setCurrentTime</c>).
    /// </summary>
    public MuxerChanges Update(long nowMs)
    {
        var changes = MuxerChanges.None;
        var newPriority = LowestPriority;
        List<int>? expired = null;
        foreach (var input in _inputs.Values)
        {
            if (input.TimeoutMs > 0 && input.TimeoutMs <= nowMs)
            {
                (expired ??= []).Add(input.Priority);
            }
            else if (input.TimeoutMs > Inactive && !Held(input))
            {
                newPriority = Math.Min(newPriority, input.Priority);
            }
        }

        if (expired is not null)
        {
            foreach (var priority in expired)
            {
                _inputs.Remove(priority);
            }

            changes |= MuxerChanges.Priorities;
        }

        if (!AutoSelect)
        {
            if (_inputs.TryGetValue(_manualPriority, out var manual))
            {
                if (!Held(manual))
                {
                    newPriority = _manualPriority;
                }
            }
            else
            {
                // the manually selected source is gone: back to automatic selection
                AutoSelect = true;
            }
        }

        var component = Get(newPriority).Component;
        if (CurrentPriority != newPriority || component != _visibleComponent)
        {
            PreviousPriority = CurrentPriority;
            CurrentPriority = newPriority;
            _visibleComponent = component;
            changes |= MuxerChanges.Visible | MuxerChanges.Priorities;
        }

        return changes;
    }

    /// <summary>True when a color, picture or effect with a timeout runs (its remaining time should be shown).</summary>
    public bool HasTimedInputs(long nowMs) =>
        _inputs.Values.Any(i => i.Priority < LowestEffectPriority && i.TimeoutMs > nowMs &&
            i.Component is SourceComponent.Effect or SourceComponent.Color or SourceComponent.Image);
}
