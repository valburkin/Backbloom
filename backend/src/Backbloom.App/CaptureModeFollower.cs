using Backbloom.Capture;
using Backbloom.Engine;
using Backbloom.Settings;

namespace Backbloom.App;

/// <summary>
/// The screen capture's settings as they apply now: <c>systemGrabber</c>, with at least Game mode's rate
/// (<c>game.fps</c>, <see cref="GameTuning.ApplyTo"/>) while Game mode is on. A separate object because the
/// <see cref="ModeMonitor"/> depends on the engine, which depends on the capture: the monitor tells this object, which
/// reconfigures the source, only when the settings that apply differ (each new configuration restarts the capture).
/// </summary>
internal sealed class CaptureModeFollower
{
    private readonly Lock _lock = new();
    private ISettingsStore? _store;
    private Action<CaptureSettings>? _configure;
    private CaptureSettings? _applied;
    private bool _game;

    public CaptureSettings SettingsFrom(ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var capture = CaptureSettings.FromJson(store.GetSection(CaptureSettings.SectionName));
        lock (_lock)
        {
            return _game ? GameTuning.FromJson(store.GetSection(GameTuning.SectionName)).ApplyTo(capture) : capture;
        }
    }

    /// <summary>Reconfigures with <paramref name="configure"/> when the capture or mode settings change.</summary>
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
            if (change.Contains(CaptureSettings.SectionName) || change.Contains(GameTuning.SectionName))
            {
                Reconfigure();
            }
        };
    }

    public void SetGameMode(bool on)
    {
        lock (_lock)
        {
            if (_game == on)
            {
                return;
            }

            _game = on;
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
