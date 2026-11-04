using System.Numerics;
using Microsoft.Extensions.Logging;
using Backbloom.Capture;
using Backbloom.Core.Borders;
using Backbloom.Core.Color;
using Backbloom.Core.Imaging;
using Backbloom.Core.Mapping;
using Backbloom.Core.Priorities;
using Backbloom.Core.Smoothing;
using Backbloom.Engine.Platform;

namespace Backbloom.Engine;

/// <summary>Everything the frame loop needs from the settings, swapped as one immutable snapshot.</summary>
/// <param name="Mapping">How the screen is mapped to the LEDs.</param>
/// <param name="Processor">The color settings, for the screen and effects.</param>
/// <param name="Passthrough">Static colors skip the color settings except the brightness and the power limit (HyperHDR disabled processing for COLOR).</param>
public sealed record PipelineConfig(LedMappingOptions Mapping, ColorProcessor Processor, ColorProcessor Passthrough)
{
    private readonly LedMappingOptions? _effectMapping;

    public PipelineConfig(LedMappingOptions mapping, ColorProcessor processor)
        : this(mapping, processor, new ColorProcessor(processor.Adjustment, colorSettings: false))
    {
    }

    /// <summary>
    /// How effects and music are mapped to the same LEDs (the strips' roles are about the screen; see
    /// <c>ControllerConfig.EffectLeds</c>); <see cref="Mapping"/> when not set.
    /// </summary>
    public LedMappingOptions EffectMapping
    {
        get => _effectMapping ?? Mapping;
        init => _effectMapping = value;
    }
}

/// <summary>
/// The single frame loop: the visible source (screen capture -> black border -> image-to-LED mapping -> the slower
/// "Screen average" LEDs (<see cref="ScreenAverageFilter"/>), an effect, or a static color) -> color processing -> the zones' colors in their LEDs (<see cref="ZoneLayer"/>) ->
/// <see cref="OutputStage"/> (smoothing, controllers, live preview). Runs on one dedicated thread and does not allocate
/// per frame (buffers are only resized when the LED count, frame size or border changes).
/// </summary>
/// <remarks>
/// Zones play only while the user's layout shows (<see cref="ShowsLayout"/>: the screen, or no source); music, an
/// effect or a color picked for every light plays on their LEDs too, and their effects pause meanwhile. Zone effects
/// step between the loop's other work without waiting: with every frame of the screen, and while nothing new comes (no
/// source, a screen without frames), when the loop wakes for the zones' next step and sends the last frame again with
/// the zones' new colors. A screen capture isn't cut short for them (that would skip capture frames), so while the
/// screen shows, zones take a step with each captured frame (catching up on missed steps). They pause while the output
/// is off or held.
/// </remarks>
public sealed partial class LightingPipeline : IDisposable
{
    private static readonly TimeSpan s_idleWait = TimeSpan.FromMilliseconds(250);

    // A probe of an inactive screen while an effect shows may delay the effect's next frame by this much at most.
    private static readonly TimeSpan s_probeBudget = TimeSpan.FromMilliseconds(20);

    // While zones animate and nothing else happens, the loop looks for a new visible source this often at least.
    private const long ZoneWaitSliceMs = 20;

    private readonly IFrameSource _source;
    private readonly ComponentStates _components;
    private readonly Func<VisibleSource> _visible;
    private readonly OutputStage _output;
    private readonly ILogger _logger;
    private readonly ScreenActivity? _screen;
    private readonly IClock _clock;
    private readonly LedMapper _mapper = new();
    private readonly ScreenAverageFilter _screenAverage = new();
    private readonly FramePool _frames = new(capacity: 1);
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _interruptLock = new();
    private readonly Thread _thread;
    private CancellationTokenSource _render = new();
    private CancellationTokenSource _probe = new();
    private volatile PipelineConfig _config;
    private volatile BlackBorderOptions? _borderOptions;
    private BlackBorderOptions? _borderOptionsInUse;
    private BlackBorderProcessor? _border;
    private VisibleSource? _rendered;
    private PipelineConfig? _renderedConfig;
    private volatile bool _refresh;
    private volatile ZoneLayer _zones = ZoneLayer.Empty;
    private Vector3[] _composed = [];
    private float _composedBacklight;
    private long _frameCount;
    private bool _started;

    /// <param name="screen">Tracks whether the screen delivers frames (null: the screen always counts as active).</param>
    /// <param name="clock">The clock zone effects step on (null: <see cref="SystemClock"/>).</param>
    public LightingPipeline(
        IFrameSource source,
        ComponentStates components,
        Func<VisibleSource> visible,
        OutputStage output,
        PipelineConfig config,
        ILogger<LightingPipeline> logger,
        ScreenActivity? screen = null,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _components = components;
        _visible = visible;
        _output = output;
        _config = config;
        _logger = logger;
        _screen = screen;
        _clock = clock ?? new SystemClock();
        _mapper.MapRebuilt += map =>
        {
            LogMapRebuilt(map.Width, map.Height, map.Layout.Count, map.SampledPixels, map.HorizontalBorder, map.VerticalBorder);
            if (map.AutoSparseLeds.Count > 0)
            {
                LogAutoSparse(map.AutoSparseLeds.Count, ImageToLedMap.SparseThreshold);
            }
        };
        _thread = new Thread(Run) { IsBackground = true, Name = "Frame loop", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Frames processed since start (captured, rendered by an effect, or a static color drawn).</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    public PipelineConfig Config => _config;

    /// <summary>Applies new settings from the next frame on.</summary>
    public void Configure(PipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        Wake();
    }

    /// <summary>The zones played over the frames while the user's layout shows (<see cref="ShowsLayout"/>).</summary>
    public ZoneLayer Zones => _zones;

    /// <summary>Plays these zones from the next frame on (the last frame is sent again with them).</summary>
    public void ConfigureZones(ZoneLayer zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        _zones = zones;
        Refresh();
    }

    /// <summary>Black border detection with these settings for the screen, or none (null).</summary>
    public void ConfigureBorders(BlackBorderOptions? options) => _borderOptions = options;

    /// <summary>
    /// Draws a static color again, e.g. after smoothing restarted and forgot it (HyperHDR re-ran
    /// <c>LightingInstance::update</c> then).
    /// </summary>
    public void Refresh()
    {
        _refresh = true;
        Wake();
    }

    /// <summary>The visible source changed: a static color is drawn at once.</summary>
    public void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Another source became visible: an effect waiting for its next step and a probe of the screen return at once,
    /// so the new source shows without waiting for them.
    /// </summary>
    public void Interrupt()
    {
        lock (_interruptLock)
        {
            _render.Cancel();
            _probe.Cancel();
        }

        Wake();
    }

    public void Start()
    {
        if (!_started)
        {
            _started = true;
            _thread.Start();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Interrupt();
        if (_started)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _stop.Dispose();
        _wake.Dispose();
        lock (_interruptLock)
        {
            _render.Dispose();
            _probe.Dispose();
        }
    }

    /// <summary>Runs one iteration of the loop; exposed for tests (the thread calls it in a loop).</summary>
    internal bool RunOnce(ref Frame? frame, ref Vector3[] colors, CancellationToken cancellationToken)
    {
        var config = _config;
        var ledCount = config.Mapping.Layout.Count;
        if (colors.Length != ledCount)
        {
            colors = new Vector3[ledCount];
        }

        // zones play with the user's layout (the screen, or nothing else shown), not over music, an effect or a color
        // the user picked for every light; their effects pause then, and while the output is off or held
        var visible = _visible();
        var zones = ShowsLayout(visible) ? _zones : ZoneLayer.Empty;
        var stepping = zones.HasEffects && _components.OutputActive;
        if (visible.Component == SourceComponent.SystemGrabber && _components.CaptureActive)
        {
            if (!Capture(ref frame, colors, config, cancellationToken))
            {
                _screen?.NoFrame();
                if (stepping)
                {
                    StepZones(zones, config, ledCount);
                }

                return !cancellationToken.IsCancellationRequested;
            }

            _screen?.FrameCaptured();
            _screenAverage.Apply(config.Mapping.Layout, colors, _clock.NowMilliseconds);
            config.Processor.Process(colors);
            Submit(colors, config.Processor, zones, config, stepping);
        }
        else if (visible.Renderer is { } renderer && _components.IsOn(Component.All))
        {
            var wakeAt = stepping ? zones.NextDue : long.MaxValue;
            if (!renderer.TryRender(config.EffectMapping, colors, wakeAt, RenderToken()) || !ReferenceEquals(visible, _visible()))
            {
                // nothing new, a zone step is due, or another source became visible meanwhile (its frame comes next)
                if (stepping)
                {
                    StepZones(zones, config, ledCount);
                }

                return !cancellationToken.IsCancellationRequested;
            }

            config.Processor.Process(colors);
            Submit(colors, config.Processor, zones, config, stepping);
            ProbeScreen(ref frame, colors, config, s_probeBudget);
        }
        else if (!ReferenceEquals(visible, _rendered) || !ReferenceEquals(config, _renderedConfig) || _refresh)
        {
            // a static color (black when there is no source): drawn once per change
            _refresh = false;
            _rendered = visible;
            _renderedConfig = config;
            var lut = Srgb.U8ToLinear16;
            colors.AsSpan().Fill(new Vector3(lut[visible.Color.R] / 65535.0f, lut[visible.Color.G] / 65535.0f, lut[visible.Color.B] / 65535.0f));
            config.Passthrough.Process(colors);
            Submit(colors, config.Passthrough, zones, config, stepping);
        }
        else
        {
            if (stepping)
            {
                IdleWithZones(ref frame, colors, zones, config, ledCount);
            }
            else if (!ProbeScreen(ref frame, colors, config, s_idleWait))
            {
                _wake.WaitOne(s_idleWait);
            }

            return !cancellationToken.IsCancellationRequested;
        }

        Interlocked.Increment(ref _frameCount);
        return true;
    }

    /// <summary>
    /// True while the user's layout shows: the screen (also while it delivers no frames), or no source at all. Music,
    /// an effect or a color then play on every LED, the zones' too (mapped with <see cref="PipelineConfig.EffectMapping"/>).
    /// </summary>
    internal static bool ShowsLayout(VisibleSource visible) =>
        visible.Component == SourceComponent.SystemGrabber || !visible.HasSource;

    // Hands a frame processed by `processor` to the output (its minimum glow, and its share of the output for the power
    // limits); with zones, a copy with the zones' colors in their LEDs (stepped first when due), kept so that zone steps
    // can send it again (with the larger share of the color settings and of static colors: zones use both).
    private void Submit(Vector3[] colors, ColorProcessor processor, ZoneLayer zones, PipelineConfig config, bool stepping)
    {
        var minimalBacklight = processor.MinimalBacklight;
        if (zones.Count == 0)
        {
            _output.Submit(colors, minimalBacklight, processor.OutputShare);
            return;
        }

        if (_composed.Length != colors.Length)
        {
            _composed = new Vector3[colors.Length];
        }

        colors.CopyTo(_composed, 0);
        _composedBacklight = minimalBacklight;
        if (stepping)
        {
            zones.Step(_clock.NowMilliseconds, config);
        }

        zones.Compose(_composed, config);
        _output.Submit(_composed, minimalBacklight, ZonesShare(config));
    }

    private static float ZonesShare(PipelineConfig config) => MathF.Max(config.Processor.OutputShare, config.Passthrough.OutputShare);

    // Steps the zone effects that are due; when one changed, the last frame goes out again with their new colors (black
    // where nothing was drawn yet).
    private void StepZones(ZoneLayer zones, PipelineConfig config, int ledCount)
    {
        if (!zones.Step(_clock.NowMilliseconds, config))
        {
            return;
        }

        if (_composed.Length != ledCount)
        {
            _composed = new Vector3[ledCount];
            _composedBacklight = 0;
        }

        zones.Compose(_composed, config);
        _output.Submit(_composed, _composedBacklight, ZonesShare(config));
    }

    // Nothing new from the visible source while zone effects run: step them when due, else probe an inactive screen or
    // wait until the next zone step (in slices, so a new visible source still shows within one).
    private void IdleWithZones(ref Frame? frame, Span<Vector3> colors, ZoneLayer zones, PipelineConfig config, int ledCount)
    {
        var now = _clock.NowMilliseconds;
        var due = zones.NextDue;
        if (now >= due)
        {
            StepZones(zones, config, ledCount);
            return;
        }

        if (ProbeScreen(ref frame, colors, config, TimeSpan.FromMilliseconds(Math.Min(due - now, (long)s_idleWait.TotalMilliseconds))) ||
            _wake.WaitOne(0))
        {
            return;
        }

        _clock.SleepUntil(Math.Min(due, now + ZoneWaitSliceMs));
    }
    // The token an effect waits on for its next step; a new one after Interrupt canceled it.
    private CancellationToken RenderToken()
    {
        lock (_interruptLock)
        {
            if (_render.IsCancellationRequested && !_stop.IsCancellationRequested)
            {
                _render.Dispose();
                _render = new CancellationTokenSource();
            }

            return _render.Token;
        }
    }

    // While the screen is registered but inactive, captures one frame now and then (within the budget) to notice when
    // it delivers again; the frame itself is not shown. Returns false when no probe was due.
    private bool ProbeScreen(ref Frame? frame, Span<Vector3> colors, PipelineConfig config, TimeSpan budget)
    {
        if (_screen is null || !_components.CaptureActive || !_screen.ProbeDue)
        {
            return false;
        }

        CancellationToken token;
        lock (_interruptLock)
        {
            if (_stop.IsCancellationRequested)
            {
                return false;
            }

            // a source that was canceled (by the budget timer or Interrupt) can't be reused
            if (!_probe.TryReset())
            {
                _probe.Dispose();
                _probe = new CancellationTokenSource();
            }

            _probe.CancelAfter(budget);
            token = _probe.Token;
        }

        _screen.Probing();
        if (Capture(ref frame, colors, config, token))
        {
            _screen.FrameCaptured();
        }

        return true;
    }

    private bool Capture(ref Frame? frame, Span<Vector3> colors, PipelineConfig config, CancellationToken cancellationToken)
    {
        var (width, height) = _source.FrameSize;
        if (frame is null)
        {
            frame = _frames.TryRent(width, height) ?? throw new InvalidOperationException("The frame pool is exhausted.");
        }
        else if (frame.Width != width || frame.Height != height)
        {
            frame.Resize(width, height);
        }

        if (!_source.TryCapture(frame, cancellationToken))
        {
            return false;
        }

        _mapper.Process(frame, config.Mapping, colors, DetectBorder(frame));
        return true;
    }

    // ImageToLedManager::verifyBorder: while detection is off, the mapping uses no border.
    private BlackBorder DetectBorder(Frame frame)
    {
        var options = _borderOptions;
        if (!ReferenceEquals(options, _borderOptionsInUse))
        {
            _borderOptionsInUse = options;
            _border = options is null ? null : new BlackBorderProcessor(options);
        }

        if (_border is null)
        {
            return BlackBorder.None;
        }

        if (_border.Process(frame))
        {
            LogBorder(_border.CurrentBorder);
        }

        return _border.CurrentBorder;
    }

    private void Run()
    {
        Frame? frame = null;
        var colors = Array.Empty<Vector3>();
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!RunOnce(ref frame, ref colors, token))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFrameFailed(ex);
                token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
            }
        }

        if (frame is not null)
        {
            _frames.Return(frame);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Image-to-LED map built for {Width}x{Height} frames, {Leds} LEDs, {Samples} samples per frame, black border {Rows} rows / {Columns} columns")]
    private partial void LogMapRebuilt(int width, int height, int leds, int samples, int rows, int columns);

    [LoggerMessage(Level = LogLevel.Information, Message = "Black border: {Border}")]
    private partial void LogBorder(BlackBorder border);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} LED areas are larger than {Threshold} pixels and are sampled sparsely (enable 'sparse processing' to apply it to all)")]
    private partial void LogAutoSparse(int count, int threshold);

    [LoggerMessage(Level = LogLevel.Error, Message = "Frame processing failed; retrying")]
    private partial void LogFrameFailed(Exception exception);
}
