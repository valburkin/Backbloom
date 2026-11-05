using Backbloom.Capture;
using Backbloom.Core.Smoothing;
using Backbloom.Engine;
using Backbloom.Settings;

namespace Backbloom.App;

/// <summary>
/// The screen capture's settings as they apply now: <c>systemGrabber</c>, with at least 60 fps while the smoothing that
/// applies (the saved one with the active profile's Look on top, <see cref="LightingEngine.SmoothingApplied"/>) has fast
/// transitions (<see cref="LookTuning.CaptureFor"/>). A separate object because the engine depends on the capture: the
/// engine tells this object (<see cref="SetSmoothing"/>), which reconfigures the source only when the settings that
/// apply differ (each new configuration restarts the capture).
/// </summary>
internal sealed class CaptureRateFollower
{
    private readonly Lock _lock = new();
    private ISettingsStore? _store;
    private Action<CaptureSettings>? _configure;
    private CaptureSettings? _applied;
    private SmoothingSettings? _smoothing;

    /// <summary>The capture settings that apply now; until the engine reports its smoothing, with the saved one.</summary>
    public CaptureSettings SettingsFrom(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var capture = CaptureSettings.FromJson(store.GetSection(CaptureSettings.SectionName));
        lock (_lock)
        {
            return LookTuning.CaptureFor(capture, _smoothing ?? SmoothingSettings.FromJson(store.GetSection("smoothing")));
        }
    }

    /// <summary>Reconfigures with <paramref name="configure"/> when the capture settings or the applied smoothing change.</summary>
    public void Follow(ISettingsStore store, Action<CaptureSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configure);
        var initial = SettingsFrom(store);
        lock (_lock)
        {
            _store = store;
            _configure = configure;
            _applied = initial;
        }

        store.Changed += change =>
        {
            if (change.Contains(CaptureSettings.SectionName))
            {
                Reconfigure();
            }
        };
    }

    /// <summary>The smoothing that applies now (<see cref="LightingEngine.SmoothingAppliedChanged"/>).</summary>
    public void SetSmoothing(SmoothingSettings smoothing)
    {
        ArgumentNullException.ThrowIfNull(smoothing);
        lock (_lock)
        {
            if (smoothing == _smoothing)
            {
                return;
            }

            _smoothing = smoothing;
        }

        Reconfigure();
    }

    private void Reconfigure()
    {
        lock (_lock)
        {
            if (_store is null || _configure is null)
            {
                return;
            }

            var next = SettingsFrom(_store);
            if (next == _applied)
            {
                return;
            }

            _applied = next;
            _configure(next);
        }
    }
}
