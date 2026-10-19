using System.Numerics;
using Microsoft.Extensions.Logging;
using Overglow.Capture;
using Overglow.Core.Color;
using Overglow.Core.Imaging;
using Overglow.Core.Mapping;

namespace Overglow.Engine;

/// <summary>Everything the frame loop needs from the settings, swapped as one immutable snapshot.</summary>
public sealed record PipelineConfig(LedMappingOptions Mapping, ColorProcessor Processor);

/// <summary>
/// The single frame loop: frame source -> image-to-LED mapping -> color processing -> every controller and the live
/// preview. Runs on one dedicated thread and does not allocate per frame (buffers are only resized when the LED
/// count or frame size changes).
/// </summary>
public sealed partial class LightingPipeline : IDisposable
{
    private const double IdleFramesPerSecond = 10;

    private readonly IFrameSource _source;
    private readonly ComponentStates _components;
    private readonly DeviceManager _devices;
    private readonly LedColorsFeed _feed;
    private readonly ILogger _logger;
    private readonly LedMapper _mapper = new();
    private readonly FramePool _frames = new(capacity: 1);
    private readonly FramePacer _idlePacer = new(IdleFramesPerSecond);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private volatile PipelineConfig _config;
    private long _frameCount;
    private bool _started;

    public LightingPipeline(
        IFrameSource source,
        ComponentStates components,
        DeviceManager devices,
        LedColorsFeed feed,
        PipelineConfig config,
        ILogger<LightingPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _components = components;
        _devices = devices;
        _feed = feed;
        _config = config;
        _logger = logger;
        _mapper.MapRebuilt += map =>
        {
            LogMapRebuilt(map.Width, map.Height, map.Layout.Count, map.SampledPixels);
            if (map.AutoSparseLeds.Count > 0)
            {
                LogAutoSparse(map.AutoSparseLeds.Count, ImageToLedMap.SparseThreshold);
            }
        };
        _thread = new Thread(Run) { IsBackground = true, Name = "Frame loop", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Frames processed since start (captured or idle).</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    public PipelineConfig Config => _config;

    /// <summary>Applies new settings from the next frame on.</summary>
    public void Configure(PipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
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
        if (_started)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        _stop.Dispose();
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

        if (_components.CaptureActive)
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
                return !cancellationToken.IsCancellationRequested;
            }

            _mapper.Process(frame, config.Mapping, colors);
            config.Processor.Process(colors);
        }
        else
        {
            if (!_idlePacer.WaitNext(cancellationToken))
            {
                return false;
            }

            colors.AsSpan().Clear();
        }

        _devices.Submit(colors);
        _feed.Publish(colors);
        Interlocked.Increment(ref _frameCount);
        return true;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Image-to-LED map built for {Width}x{Height} frames, {Leds} LEDs, {Samples} samples per frame")]
    private partial void LogMapRebuilt(int width, int height, int leds, int samples);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} LED areas are larger than {Threshold} pixels and are sampled sparsely (enable 'sparse processing' to apply it to all)")]
    private partial void LogAutoSparse(int count, int threshold);

    [LoggerMessage(Level = LogLevel.Error, Message = "Frame processing failed; retrying")]
    private partial void LogFrameFailed(Exception exception);
}
