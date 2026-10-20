using System.Numerics;
using Microsoft.Extensions.Logging;
using Overglow.Capture;
using Overglow.Core.Borders;
using Overglow.Core.Color;
using Overglow.Core.Imaging;
using Overglow.Core.Mapping;
using Overglow.Core.Priorities;

namespace Overglow.Engine;

/// <summary>Everything the frame loop needs from the settings, swapped as one immutable snapshot.</summary>
/// <param name="Processor">The color settings, for the screen and effects.</param>
/// <param name="Passthrough">Static colors skip the color settings (HyperHDR disabled processing for COLOR).</param>
public sealed record PipelineConfig(LedMappingOptions Mapping, ColorProcessor Processor, ColorProcessor Passthrough)
{
    public PipelineConfig(LedMappingOptions mapping, ColorProcessor processor)
        : this(mapping, processor, new ColorProcessor(processor.Adjustment, enabled: false))
    {
    }
}

/// <summary>
/// The single frame loop: the visible source (screen capture -> black border -> image-to-LED mapping, an effect, or a
/// static color) -> color processing -> <see cref="OutputStage"/> (smoothing, controllers, live preview). Runs on one
/// dedicated thread and does not allocate per frame (buffers are only resized when the LED count, frame size or
/// border changes).
/// </summary>
public sealed partial class LightingPipeline : IDisposable
{
    private static readonly TimeSpan s_idleWait = TimeSpan.FromMilliseconds(250);

    private readonly IFrameSource _source;
    private readonly ComponentStates _components;
    private readonly Func<VisibleSource> _visible;
    private readonly OutputStage _output;
    private readonly ILogger _logger;
    private readonly LedMapper _mapper = new();
    private readonly FramePool _frames = new(capacity: 1);
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private volatile PipelineConfig _config;
    private volatile BlackBorderOptions? _borderOptions;
    private BlackBorderOptions? _borderOptionsInUse;
    private BlackBorderProcessor? _border;
    private VisibleSource? _rendered;
    private PipelineConfig? _renderedConfig;
    private volatile bool _refresh;
    private long _frameCount;
    private bool _started;

    public LightingPipeline(
        IFrameSource source,
        ComponentStates components,
        Func<VisibleSource> visible,
        OutputStage output,
        PipelineConfig config,
        ILogger<LightingPipeline> logger)
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
        Wake();
        if (_started)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _stop.Dispose();
        _wake.Dispose();
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

        var visible = _visible();
        if (visible.Component == SourceComponent.SystemGrabber && _components.CaptureActive)
        {
            if (!Capture(ref frame, colors, config, cancellationToken))
            {
                return !cancellationToken.IsCancellationRequested;
            }

            config.Processor.Process(colors);
            _output.Submit(colors, config.Processor.MinimalBacklight);
        }
        else if (visible.Renderer is { } renderer)
        {
            if (!renderer.TryRender(colors, cancellationToken))
            {
                return !cancellationToken.IsCancellationRequested;
            }

            config.Processor.Process(colors);
            _output.Submit(colors, config.Processor.MinimalBacklight);
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
            _output.Submit(colors, config.Passthrough.MinimalBacklight);
        }
        else
        {
            _wake.WaitOne(s_idleWait);
            return !cancellationToken.IsCancellationRequested;
        }

        Interlocked.Increment(ref _frameCount);
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
