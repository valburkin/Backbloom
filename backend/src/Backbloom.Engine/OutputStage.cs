using System.Numerics;
using Microsoft.Extensions.Logging;
using Backbloom.Core.Smoothing;
using Backbloom.Engine.Platform;

namespace Backbloom.Engine;

/// <summary>
/// The end of the pipeline: smoothing, the anti-flicker filter, the live preview feed and the controllers.
/// Without smoothing, every processed frame goes straight out on the frame loop thread. With smoothing, the frame loop
/// only hands its colors to the <see cref="Smoother"/> and the output thread ticks at the smoothing update frequency
/// (absolute deadlines on the injected <see cref="IClock"/>), so the controllers are refreshed at that rate whatever
/// the capture rate is, as the device timer did in HyperHDR.
/// </summary>
/// <remarks>
/// One smoother for every controller (HyperHDR ran one per instance). Neither path allocates per frame or tick; the
/// smoother is guarded by one lock that both threads hold only for the interpolation itself.
/// </remarks>
public sealed partial class OutputStage : IDisposable
{
    private readonly DeviceManager _devices;
    private readonly LedColorsFeed _feed;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Smoother _smoother = new();
    private readonly AntiFlickerFilter _filter = new();
    private readonly Lock _lock = new();
    private readonly Lock _emitLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private Vector3[] _tickColors = [];
    private Vector3[] _emitColors = [];
    private volatile float[]? _gains;
    private volatile bool _antiFlicker;
    private volatile bool _stopping;
    private bool _outputEnabled = true;
    private bool _started;
    private long _ticks;

    public OutputStage(DeviceManager devices, LedColorsFeed feed, IClock clock, ILogger<OutputStage> logger)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _devices = devices;
        _feed = feed;
        _clock = clock;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Smoothing", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>The <c>SMOOTHING</c> component state.</summary>
    public bool SmoothingEnabled
    {
        get
        {
            lock (_lock)
            {
                return _smoother.Enabled;
            }
        }
    }

    /// <summary>Ticks between frames now (0 while frames go out directly).</summary>
    public long UpdateIntervalMs
    {
        get
        {
            lock (_lock)
            {
                return _smoother.UpdateIntervalMs;
            }
        }
    }

    /// <summary>Smoothing ticks that sent a frame.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    public void Start()
    {
        if (!_started)
        {
            _started = true;
            _thread.Start();
        }
    }

    /// <summary>Applies the <c>smoothing</c> setting.</summary>
    public void Configure(SmoothingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SmoothingConfig config;
        lock (_lock)
        {
            _smoother.Configure(settings);
            _antiFlicker = _smoother.AntiFlickeringFilter;
            config = _smoother.CurrentConfig;
        }

        LogConfigured(settings.Enable, config.Type, config.SettlingTimeMs, config.UpdateIntervalMs, settings.AntiFlickeringFilter, settings.ContinuousOutput);
        _wake.Set();
    }

    /// <summary>
    /// Brightness per LED (the strips' <c>brightness</c>), applied to everything sent to the controllers and the live
    /// preview after smoothing; null: full brightness. Used only while its length matches the frames.
    /// </summary>
    public void SetLedGains(float[]? gains) => _gains = gains;

    /// <summary>Switches smoothing on or off (the <c>SMOOTHING</c> component).</summary>
    public void SetSmoothingEnabled(bool enabled)
    {
        lock (_lock)
        {
            _smoother.SetEnabled(enabled);
        }

        _wake.Set();
    }

    /// <summary>Uses the smoothing configuration of the visible source (0: the user's).</summary>
    public void UseSmoothingConfig(int configId)
    {
        lock (_lock)
        {
            _smoother.UseConfig(configId);
        }

        _wake.Set();
    }

    /// <summary>Registers an effect's smoothing configuration (see <see cref="Smoother.AddCustomConfig"/>).</summary>
    public int AddSmoothingConfig(int configId, long settlingTimeMs, double updateFrequency, bool pause)
    {
        lock (_lock)
        {
            return _smoother.AddCustomConfig(configId, settlingTimeMs, updateFrequency, pause);
        }
    }

    /// <summary>The controllers were switched on or off: the smoothing starts over.</summary>
    /// <returns>False when the output already had that state.</returns>
    public bool SetOutputEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_outputEnabled == enabled)
            {
                return false;
            }

            _outputEnabled = enabled;
            _smoother.Clear(enabled && _smoother.IsActive);
        }

        lock (_emitLock)
        {
            _filter.Reset();
        }

        _wake.Set();
        return true;
    }

    /// <summary>
    /// A processed frame (nonlinear colors 0..1). Called from the frame loop; does not allocate.
    /// </summary>
    /// <param name="minimalBacklight">The color settings' minimal brightness, which the interpolators keep.</param>
    public void Submit(ReadOnlySpan<Vector3> colors, float minimalBacklight)
    {
        bool direct;
        lock (_lock)
        {
            direct = !_outputEnabled || !_smoother.IsActive;
            if (!direct)
            {
                _smoother.SetTarget(colors, minimalBacklight, _clock.NowMilliseconds);
            }
        }

        if (direct)
        {
            Emit(colors);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_started)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _wake.Dispose();
    }

    /// <summary>One smoothing tick at <paramref name="nowMs"/>; exposed for tests (the output thread calls it).</summary>
    /// <returns>True when a frame was sent.</returns>
    internal bool Tick(long nowMs)
    {
        int count;
        lock (_lock)
        {
            if (!_outputEnabled)
            {
                return false;
            }

            var needed = _smoother.Interpolator.Count;
            if (_tickColors.Length < needed)
            {
                _tickColors = new Vector3[needed];
            }

            count = _smoother.Tick(nowMs, _tickColors);
        }

        if (count == 0)
        {
            return false;
        }

        Emit(_tickColors.AsSpan(0, count));
        Interlocked.Increment(ref _ticks);
        return true;
    }

    private void Emit(ReadOnlySpan<Vector3> colors)
    {
        lock (_emitLock)
        {
            if (_emitColors.Length != colors.Length)
            {
                _emitColors = new Vector3[colors.Length];
            }

            colors.CopyTo(_emitColors);
            if (_antiFlicker)
            {
                _filter.Apply(_emitColors);
            }

            if (_gains is { } gains && gains.Length == _emitColors.Length)
            {
                for (var i = 0; i < gains.Length; i++)
                {
                    _emitColors[i] *= gains[i];
                }
            }

            _devices.Submit(_emitColors);
            _feed.Publish(_emitColors);
        }
    }

    private void Run()
    {
        long next = 0;
        while (!_stopping)
        {
            long interval;
            lock (_lock)
            {
                interval = _outputEnabled ? _smoother.UpdateIntervalMs : 0;
            }

            if (interval <= 0)
            {
                _wake.WaitOne();
                next = 0;
                continue;
            }

            var now = _clock.NowMilliseconds;
            next = next == 0 || now - next > 2 * interval ? now + interval : next + interval;
            _clock.SleepUntil(next);
            if (_stopping)
            {
                break;
            }

            try
            {
                Tick(_clock.NowMilliseconds);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogTickFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Smoothing: enabled {Enabled}, {Type}, {Time} ms, a frame every {Interval} ms, anti-flickering {AntiFlicker}, continuous output {Continuous}")]
    private partial void LogConfigured(bool enabled, SmoothingType type, long time, long interval, bool antiFlicker, bool continuous);

    [LoggerMessage(Level = LogLevel.Error, Message = "Smoothing tick failed")]
    private partial void LogTickFailed(Exception exception);
}
