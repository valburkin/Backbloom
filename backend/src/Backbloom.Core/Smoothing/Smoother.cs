using System.Numerics;

namespace Backbloom.Core.Smoothing;

/// <summary>
/// The smoothing controller: the user's configuration and the ones effects register, the current interpolator, and
/// when a tick sends a frame. Port of HyperHDR's <c>InfiniteSmoothing</c> (sources/infinite-color-engine/
/// InfiniteSmoothing.cpp, MIT, awawa-dev) without its timer, signals and mutex: the caller drives it with
/// <see cref="SetTarget"/> (every processed frame) and <see cref="Tick"/> (every <see cref="UpdateIntervalMs"/>) and
/// serializes the calls.
/// </summary>
/// <remarks>
/// The configuration is selected when the visible source or the settings change, never per frame (the C++ code asked
/// on every LED update, and every mismatch restarted the interpolator and the device timer).
/// </remarks>
public sealed class Smoother
{
    /// <summary>Id of the user's configuration (the <c>smoothing</c> setting).</summary>
    public const int UserConfig = 0;

    /// <summary>First id of the configurations effects register.</summary>
    public const int EffectConfigsStart = 1;

    private const int CooldownPhase = 3;
    private const long KeepAliveMs = 1000;

    private readonly List<SmoothingConfig> _configs = [SmoothingConfig.Initial];
    private Interpolator _interpolator = new StepperInterpolator();
    private bool _enabled;
    private int _coolDown = CooldownPhase;
    private long _lastSentFrame;
    private float _minimalBacklight;

    /// <summary>The <c>SMOOTHING</c> component: smoothing is wanted.</summary>
    public bool Enabled => _enabled;

    /// <summary>Smoothing runs: enabled and the current configuration isn't a "direct output" one.</summary>
    public bool IsActive => _enabled && !CurrentConfig.Pause;

    /// <summary>Keep sending while the colors are settled (otherwise only one frame per second).</summary>
    public bool ContinuousOutput { get; private set; }

    /// <summary>The <c>antiFlickeringFilter</c> setting (applied to the 8-bit output, see <see cref="AntiFlickerFilter"/>).</summary>
    public bool AntiFlickeringFilter { get; private set; }

    public int CurrentConfigId { get; private set; } = UserConfig;

    public SmoothingConfig CurrentConfig => _configs[CurrentConfigId];

    /// <summary>The interpolator in use (replaced when a configuration is selected).</summary>
    public Interpolator Interpolator => _interpolator;

    /// <summary>Time between two ticks while smoothing runs, else 0 (C++ <c>getSuggestedInterval</c>).</summary>
    public long UpdateIntervalMs => IsActive ? CurrentConfig.UpdateIntervalMs : 0;

    /// <summary>Applies the <c>smoothing</c> setting (C++ <c>handleSignalInstanceSettingsChanged</c>).</summary>
    public void Configure(SmoothingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_enabled != settings.Enable)
        {
            SetEnabled(settings.Enable);
        }

        ContinuousOutput = settings.ContinuousOutput;
        AntiFlickeringFilter = settings.AntiFlickeringFilter;
        _configs[UserConfig] = settings.Config;
        if (CurrentConfigId == UserConfig)
        {
            SelectConfig(UserConfig);
        }
    }

    /// <summary>Switches smoothing on or off; the colors are forgotten either way.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Clear(IsActive);
    }

    /// <summary>
    /// Forgets the colors (the next frame is shown at once), e.g. when the output is switched on or off
    /// (C++ <c>clearQueuedColors</c>).
    /// </summary>
    public void Clear(bool outputEnabled)
    {
        _coolDown = outputEnabled ? CooldownPhase : 0;
        _lastSentFrame = 0;
        _interpolator.ResetState();
    }

    /// <summary>Uses configuration <paramref name="configId"/> (the user's when unknown) with a new interpolator.</summary>
    /// <returns>False when the id was unknown.</returns>
    public bool SelectConfig(int configId)
    {
        var known = configId >= 0 && configId < _configs.Count;
        CurrentConfigId = known ? configId : UserConfig;
        _coolDown = IsActive ? CooldownPhase : 0;
        _lastSentFrame = 0;
        _interpolator = Interpolator.Create(CurrentConfig);
        return known;
    }

    /// <summary>Selects <paramref name="configId"/> unless it is already the current one.</summary>
    public void UseConfig(int configId)
    {
        if (configId != CurrentConfigId)
        {
            SelectConfig(configId);
        }
    }

    /// <summary>
    /// Registers (or, for an existing <paramref name="configId"/>, replaces) a configuration for an effect and returns
    /// its id; an equal one is reused (C++ <c>addCustomSmoothingConfig</c>).
    /// </summary>
    /// <param name="pause">Show the effect's colors directly.</param>
    public int AddCustomConfig(int configId, long settlingTimeMs, double updateFrequency, bool pause)
    {
        var config = SmoothingConfig.ForEffect(settlingTimeMs, updateFrequency, pause);
        if (configId >= 0 && configId < _configs.Count)
        {
            _configs[configId] = config;
            return configId;
        }

        for (var id = EffectConfigsStart; id < _configs.Count; id++)
        {
            var c = _configs[id];
            if ((c.SettlingTimeMs == config.SettlingTimeMs && c.UpdateIntervalMs == config.UpdateIntervalMs) || (pause && c.Pause))
            {
                return id;
            }
        }

        _configs.Add(config);
        return _configs.Count - 1;
    }

    /// <summary>A processed frame arrived at <paramref name="nowMs"/>: its colors become the target.</summary>
    /// <param name="minimalBacklight">The color settings' minimal brightness (0 when off).</param>
    public void SetTarget(ReadOnlySpan<Vector3> colors, float minimalBacklight, long nowMs)
    {
        _minimalBacklight = minimalBacklight;
        if (IsActive)
        {
            _interpolator.SetTargetColors(colors, nowMs);
        }
    }

    /// <summary>
    /// One smoothing tick at <paramref name="nowMs"/>: advances the colors and writes them to
    /// <paramref name="output"/> when they should be sent. Once the colors settle, a few more frames are sent, then one
    /// per second unless <see cref="ContinuousOutput"/>.
    /// </summary>
    /// <returns>The number of colors to send, 0 for none.</returns>
    public int Tick(long nowMs, Span<Vector3> output)
    {
        if (!IsActive)
        {
            return 0;
        }

        _interpolator.UpdateCurrentColors(nowMs, _minimalBacklight);
        var count = _interpolator.GetCurrentColors(output, _minimalBacklight);
        var finished = false;
        if (!_interpolator.IsAnimationComplete)
        {
            _coolDown = CooldownPhase;
        }
        else if (!ContinuousOutput)
        {
            if (_coolDown > 0)
            {
                _coolDown--;
            }
            else if (Math.Abs(nowMs - _lastSentFrame) < KeepAliveMs)
            {
                finished = true;
            }
        }

        if (count == 0 || finished)
        {
            return 0;
        }

        _lastSentFrame = nowMs;
        return count;
    }
}

/// <summary>
/// Keeps an LED at its previous value until it changes by about half an 8-bit step, so smoothing doesn't make 8-bit
/// LEDs flicker between two neighboring values. Port of the filter in <c>LedDevice::write</c>; applied once before
/// the frame fans out to the controllers (every 8-bit controller had the same state in C++).
/// </summary>
public sealed class AntiFlickerFilter
{
    private const float Threshold = 0.49f;

    private Vector3[] _held = [];
    private int _count = -1;

    /// <summary>Filters <paramref name="colors"/> in place.</summary>
    public void Apply(Span<Vector3> colors)
    {
        if (_count != colors.Length)
        {
            if (_held.Length < colors.Length)
            {
                _held = new Vector3[colors.Length];
            }

            colors.CopyTo(_held);
            _count = colors.Length;
            return;
        }

        for (var i = 0; i < colors.Length; i++)
        {
            var next = colors[i];
            if (CppMath.MaxAbs(next - _held[i]) * 255.0f > Threshold)
            {
                _held[i] = next;
            }

            colors[i] = _held[i];
        }
    }

    /// <summary>Forgets the held colors (the next frame passes unchanged).</summary>
    public void Reset() => _count = -1;
}
