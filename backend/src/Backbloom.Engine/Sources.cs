using System.Numerics;
using Backbloom.Core.Color;
using Backbloom.Core.Mapping;
using Backbloom.Core.Priorities;
using Backbloom.Engine.Platform;

namespace Backbloom.Engine;

/// <summary>
/// A source that renders LED colors itself (effects and music mode, <see cref="EffectSource"/>). Registered with
/// <see cref="PriorityManager.Register"/>; the frame loop calls it only while it is the visible source.
/// </summary>
public interface ILightSource
{
    /// <summary>
    /// Renders the next frame into <paramref name="linearColors"/> (one linear-light color 0..1 per LED of the
    /// layout in <paramref name="mapping"/>, which a source that paints pictures maps them with); the color settings
    /// and smoothing are applied afterwards. Blocks until the frame is due (like <c>IFrameSource.TryCapture</c>) and
    /// returns false when there is nothing new, <paramref name="cancellationToken"/> was canceled (another source
    /// became visible) or the clock reached <paramref name="wakeAtMs"/> before the frame was due (the frame loop has
    /// other work then: the zones' next step). Must not allocate in steady state.
    /// </summary>
    bool TryRender(LedMappingOptions mapping, Span<Vector3> linearColors, long wakeAtMs, CancellationToken cancellationToken);
}

/// <summary>What the frame loop shows now: a snapshot, replaced (never changed) when the visible source changes.</summary>
/// <param name="Version">Increments with every change, so the frame loop renders a static color once per change.</param>
public sealed record VisibleSource(int Priority, SourceComponent Component, Rgb8 Color, ILightSource? Renderer, int SmoothingConfig, long Version)
{
    public static VisibleSource None { get; } = new(Muxer.LowestPriority, SourceComponent.Color, Rgb8.Black, null, 0, 0);

    /// <summary>False for the "no source" entry: the controllers are switched off.</summary>
    public bool HasSource => Priority != Muxer.LowestPriority;
}

/// <summary>A source as <c>serverinfo.priorities</c> lists it.</summary>
/// <param name="DurationMs">Remaining time of a source with a timeout.</param>
/// <param name="Color">The color of a COLOR source.</param>
public sealed record PriorityInfo(int Priority, string ComponentId, string Origin, string? Owner, bool Active, bool Visible, long? DurationMs, Rgb8? Color);

/// <summary>
/// The sources competing for the LEDs (screen, colors, effects): a thread-safe front of <see cref="Muxer"/> with its
/// 250 ms timeout timer, the renderers of custom sources, and change events raised outside the lock.
/// </summary>
public sealed class PriorityManager : IDisposable
{
    private static readonly TimeSpan s_updateInterval = TimeSpan.FromMilliseconds(250);
    private const long TimedRefreshMs = 1000;

    private readonly Muxer _muxer = new();
    private readonly Dictionary<int, ILightSource> _renderers = [];
    private readonly IClock _clock;
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private volatile VisibleSource _visible = VisibleSource.None;
    private long _lastTimedRefresh;
    private bool _disposed;

    public PriorityManager(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _timer = new Timer(_ => OnTimer(), null, s_updateInterval, s_updateInterval);
    }

    /// <summary>Raised after the list of sources or their state changed (the <c>priorities-update</c> event).</summary>
    public event Action? PrioritiesChanged;

    /// <summary>Raised with the new snapshot after another source became visible.</summary>
    public event Action<VisibleSource>? VisibleChanged;

    public VisibleSource Visible => _visible;

    public bool AutoSelect
    {
        get
        {
            lock (_lock)
            {
                return _muxer.AutoSelect;
            }
        }
    }

    /// <summary>The sources, the visible one first (as HyperHDR listed them); the "no source" entry is left out.</summary>
    public IReadOnlyList<PriorityInfo> Priorities
    {
        get
        {
            lock (_lock)
            {
                var now = _clock.NowMilliseconds;
                var current = _muxer.CurrentPriority;
                var list = new List<PriorityInfo>();
                foreach (var input in _muxer.Inputs)
                {
                    if (input.Priority == Muxer.LowestPriority)
                    {
                        continue;
                    }

                    var info = new PriorityInfo(
                        input.Priority,
                        input.Component.ToId(),
                        input.Origin,
                        input.Owner,
                        input.Active,
                        input.Priority == current,
                        input.TimeoutMs > 0 ? input.TimeoutMs - now : null,
                        input.Component == SourceComponent.Color ? input.StaticColor : null);
                    if (info.Visible)
                    {
                        list.Insert(0, info);
                    }
                    else
                    {
                        list.Add(info);
                    }
                }

                return list;
            }
        }
    }

    /// <summary>
    /// Registers a source at <paramref name="priority"/> (0 is the highest, 255 means "nothing"); it is inactive until
    /// <see cref="SetActive"/>. Effects pass their renderer and the id of their smoothing configuration.
    /// </summary>
    public void Register(int priority, SourceComponent component, string origin, string? owner, ILightSource? renderer = null, int smoothingConfig = 0)
    {
        Change(now =>
        {
            if (renderer is null)
            {
                _renderers.Remove(priority);
            }
            else
            {
                _renderers[priority] = renderer;
            }

            return _muxer.Register(priority, component, origin, owner, Rgb8.Black, smoothingConfig);
        }, refreshPriority: priority);
    }

    /// <summary>The renderer registered at <paramref name="priority"/>, or null (also after the source was cleared or expired).</summary>
    public ILightSource? GetRenderer(int priority)
    {
        lock (_lock)
        {
            return _renderers.GetValueOrDefault(priority);
        }
    }

    /// <summary>The source delivers for <paramref name="timeoutMs"/> ms (&lt;= 0: until cleared).</summary>
    /// <returns>False when the priority isn't registered.</returns>
    public bool SetActive(int priority, long timeoutMs = Muxer.NoTimeout)
    {
        var known = false;
        Change(now =>
        {
            var changes = _muxer.SetInput(priority, timeoutMs, now);
            known = changes is not null;
            return changes ?? MuxerChanges.None;
        });
        return known;
    }

    /// <summary>The source stops delivering but stays registered (it shows as inactive).</summary>
    public void SetInactive(int priority) => Change(now => _muxer.SetInactive(priority, now) ?? MuxerChanges.None);

    /// <summary>
    /// Shows a static color at <paramref name="priority"/> for <paramref name="durationMs"/> ms (&lt;= 0: until
    /// cleared). Port of <c>LightingInstance::setColor</c>.
    /// </summary>
    public void SetColor(int priority, Rgb8 color, long durationMs, string origin)
    {
        Change(now =>
        {
            var changes = MuxerChanges.None;
            if (_muxer.Get(priority).Component != SourceComponent.Color)
            {
                changes |= _muxer.Clear(priority, now) ?? MuxerChanges.None;
                _renderers.Remove(priority);
            }

            changes |= _muxer.Register(priority, SourceComponent.Color, origin, null, color, 0);
            return changes | (_muxer.SetInput(priority, durationMs, now) ?? MuxerChanges.None);
        }, refreshPriority: priority);
    }

    /// <summary>Removes the source at <paramref name="priority"/>.</summary>
    public bool Clear(int priority)
    {
        var cleared = false;
        Change(now =>
        {
            var changes = _muxer.Clear(priority, now);
            cleared = changes is not null;
            _renderers.Remove(priority);
            return changes ?? MuxerChanges.None;
        });
        return cleared;
    }

    /// <summary>Removes every color, picture and effect (the API's clear with priority -1).</summary>
    public void ClearAll() => Change(now => _muxer.ClearAll(now));

    /// <summary>Selects <paramref name="priority"/> manually (<c>sourceselect</c>).</summary>
    public bool Select(int priority)
    {
        var selected = false;
        Change(now =>
        {
            selected = _muxer.Select(priority, now, out var changes);
            return changes;
        });
        return selected;
    }

    /// <summary>Switches automatic source selection on or off (<c>sourceselect auto</c>).</summary>
    public bool SetAutoSelect(bool enabled)
    {
        var changed = false;
        Change(now =>
        {
            changed = _muxer.SetAutoSelect(enabled, now, out var changes);
            return changes;
        });
        return changed;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    /// <summary>Runs the timeout check now; exposed for tests (the timer calls it every 250 ms).</summary>
    internal void OnTimer()
    {
        Change(now =>
        {
            var changes = _muxer.Update(now);
            if (_muxer.HasTimedInputs(now) && now - _lastTimedRefresh >= TimedRefreshMs)
            {
                // the remaining time of colors and effects with a duration counts down in the UI
                _lastTimedRefresh = now;
                changes |= MuxerChanges.Priorities;
            }

            return changes;
        });
    }

    /// <param name="refreshPriority">
    /// A source whose description changed: when it is the visible one, the frame loop gets a new snapshot (and renders a
    /// static color again) even though the visible priority stayed.
    /// </param>
    private void Change(Func<long, MuxerChanges> change, int? refreshPriority = null)
    {
        MuxerChanges changes;
        VisibleSource? visible = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            changes = change(_clock.NowMilliseconds);
            if ((changes & MuxerChanges.Priorities) != 0)
            {
                // sources that expired or were cleared leave their renderer behind
                foreach (var priority in _renderers.Keys.Where(p => !_muxer.Has(p)).ToArray())
                {
                    _renderers.Remove(priority);
                }
            }

            var refresh = refreshPriority == _muxer.CurrentPriority;
            if ((changes & MuxerChanges.Visible) != 0 || refresh)
            {
                var input = _muxer.Visible;
                var next = new VisibleSource(
                    input.Priority,
                    input.Component,
                    input.StaticColor,
                    _renderers.GetValueOrDefault(input.Priority),
                    input.SmoothingConfig,
                    _visible.Version + 1);
                if (refresh || next with { Version = _visible.Version } != _visible)
                {
                    _visible = next;
                    visible = next;
                }
            }
        }

        if (visible is not null)
        {
            VisibleChanged?.Invoke(visible);
        }

        if ((changes & MuxerChanges.Priorities) != 0 || visible is not null || refreshPriority is not null)
        {
            PrioritiesChanged?.Invoke();
        }
    }
}
