using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Backbloom.Capture.Hdr;
using Backbloom.Core.Imaging;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Capture.Windows;

/// <summary>Counters of a running screen capture (since the last <see cref="DxgiScreenSource.TakeStatistics"/>).</summary>
/// <param name="Frames">Frames delivered (new and repeated images).</param>
/// <param name="NewImages">Frames with a new desktop image.</param>
/// <param name="AverageLatency">Mean time from the desktop present to the frame being delivered, over new images.</param>
/// <param name="MaxLatency">Longest such time.</param>
/// <param name="AverageWork">Mean time spent capturing, converting and reading back one frame (excluding pacing).</param>
/// <param name="Elapsed">Length of the measured interval.</param>
public readonly record struct CaptureStatistics(long Frames, long NewImages, TimeSpan AverageLatency, TimeSpan MaxLatency, TimeSpan AverageWork, TimeSpan Elapsed);

/// <summary>
/// Screen capture with DXGI desktop duplication and Direct3D 11 (port of DxGrabber). All capture work runs on the
/// thread that calls <see cref="TryCapture"/> (the frame loop): initialization is lazy, failures are retried after a
/// delay, and a lost duplication (display mode or HDR change, desktop switch, device removal) is rebuilt. Doesn't
/// allocate per frame.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed unsafe partial class DxgiScreenSource : IScreenCaptureSource
{
    private static readonly long s_statisticsInterval = Stopwatch.Frequency * 10;

    private readonly LutProvider _lutProvider;
    private readonly ILogger _logger;
    private readonly InputDesktop _desktop = new();
    private readonly List<DisplayCapture> _displays = [];
    private readonly Lock _statisticsLock = new();
    private volatile CaptureSettings _pending;
    private volatile IReadOnlyList<MonitorInfo> _monitors = [];
    private volatile string _device = "none";
    private volatile CaptureHdrStatus _hdrStatus;
    private CaptureSettings? _active;

    // The device setting and the device path of the one monitor it captured last (see MonitorEnumerator.SelectFor).
    private (string? Device, string? Path) _captured;
    private string? _loggedFallback;
    private GpuDevice? _gpu;
    private int[] _offsets = [];
    private FramePacer _pacer;
    private (int Width, int Height) _frameSize = (1, 1);
    private Frame? _clearedFrame;
    private long _retryAt;
    private bool _needsLut;
    private string? _lastProblem;
    private bool _disposed;

    // Statistics, written by the capture thread.
    private long _statFrames;
    private long _statNewImages;
    private long _statLatencySum;
    private long _statLatencyMax;
    private long _statWorkSum;
    private long _statStarted = Stopwatch.GetTimestamp();
    private long _statLogged = Stopwatch.GetTimestamp();

    public DxgiScreenSource(CaptureSettings settings, LutProvider lutProvider, ILogger<DxgiScreenSource> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(lutProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _pending = settings;
        _hdrStatus = CaptureHdrStatus.Idle(settings.ToneMapping);
        _lutProvider = lutProvider;
        _logger = logger;
        _pacer = new FramePacer(settings.FramesPerSecond);
        try
        {
            _monitors = [.. MonitorEnumerator.Enumerate().Select(o => o.Info)];
        }
        catch (CaptureException ex)
        {
            LogEnumerationFailed(ex);
        }
    }

    public string Name => "DirectX11";

    /// <summary>The captured monitor id (or multi-monitor id), "none" while nothing is captured.</summary>
    public string Device => _device;

    public (int Width, int Height) FrameSize => _frameSize;

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public CaptureHdrStatus HdrStatus => _hdrStatus;

    public event Action<CaptureHdrStatus>? HdrStatusChanged;

    /// <summary>True while a duplication is running.</summary>
    public bool IsCapturing => _displays.Count > 0;

    /// <summary>Description of each captured monitor: id, format, path (for diagnostics and tests).</summary>
    public IReadOnlyList<string> DescribeDisplays() =>
        [.. _displays.Select(d => $"{d.Monitor.Id}: {d.Format}{(d.WideGamut ? " (HDR)" : "")}{(d.AppliesLut ? " + LUT" : "")}, {(d.UsesGpu ? "GPU" : "CPU")}, {d.Region}")];

    public void Configure(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _pending = settings;
    }

    public bool TryCapture(Frame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = _pending;
        if (!ReferenceEquals(settings, _active))
        {
            Teardown();
            _active = settings;
            _pacer = new FramePacer(settings.FramesPerSecond);
            _retryAt = 0;
        }

        if (_displays.Count == 0)
        {
            if (!WaitUntil(_retryAt, cancellationToken) || !TryStart(settings))
            {
                return false;
            }
        }

        if (!_pacer.WaitNext(cancellationToken))
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        PrepareFrame(frame);
        // The table only applies to 8-bit captures that tone mapping is on for; HDR (wide gamut) captures are converted
        // by the shader alone.
        var lut = _needsLut ? _lutProvider.TryGet() : null;
        var newImages = 0;
        var images = 0;
        long latencySum = 0, latencyMax = 0;
        for (var i = 0; i < _displays.Count; i++)
        {
            DisplayResult result;
            long presentTime;
            try
            {
                result = _displays[i].Capture(frame, _offsets[i], lut, out presentTime);
            }
            catch (CaptureException ex)
            {
                LogCaptureFailed(ex);
                result = DisplayResult.Lost;
                presentTime = 0;
            }

            switch (result)
            {
                case DisplayResult.Lost:
                    LogLost(_displays[i].Monitor.Id, _gpu?.RemovedReason.Value ?? 0);
                    Teardown();
                    _retryAt = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 10);
                    return false;
                case DisplayResult.NewImage:
                    newImages++;
                    images++;
                    if (presentTime != 0)
                    {
                        var latency = Stopwatch.GetTimestamp() - presentTime;
                        latencySum += latency;
                        latencyMax = Math.Max(latencyMax, latency);
                    }

                    break;
                case DisplayResult.SameImage:
                    images++;
                    break;
                case DisplayResult.NoImage:
                    break;
            }
        }

        if (images == 0)
        {
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        frame.Timestamp = now;
        Record(newImages > 0, newImages == 0 ? 0 : latencySum / newImages, latencyMax, now - started, now);
        return true;
    }

    /// <summary>Returns the counters since the last call and starts a new interval.</summary>
    public CaptureStatistics TakeStatistics()
    {
        lock (_statisticsLock)
        {
            var now = Stopwatch.GetTimestamp();
            var frames = _statFrames;
            var statistics = new CaptureStatistics(
                frames,
                _statNewImages,
                Ticks(_statNewImages == 0 ? 0 : _statLatencySum / _statNewImages),
                Ticks(_statLatencyMax),
                Ticks(frames == 0 ? 0 : _statWorkSum / frames),
                Ticks(now - _statStarted));
            _statFrames = _statNewImages = _statLatencySum = _statLatencyMax = _statWorkSum = 0;
            _statStarted = now;
            return statistics;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Teardown();
        _desktop.Dispose();
    }

    private static TimeSpan Ticks(long stopwatchTicks) => TimeSpan.FromSeconds((double)stopwatchTicks / Stopwatch.Frequency);

    private static bool WaitUntil(long timestamp, CancellationToken cancellationToken)
    {
        var ms = (timestamp - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
        if (ms > 0 && cancellationToken.WaitHandle.WaitOne((int)Math.Min(ms, int.MaxValue)))
        {
            return false;
        }

        return !cancellationToken.IsCancellationRequested;
    }

    private void Record(bool newImage, long latency, long latencyMax, long work, long now)
    {
        lock (_statisticsLock)
        {
            _statFrames++;
            _statWorkSum += work;
            if (newImage)
            {
                _statNewImages++;
                _statLatencySum += latency;
                _statLatencyMax = Math.Max(_statLatencyMax, latencyMax);
            }
        }

        if (now - _statLogged >= s_statisticsInterval && _logger.IsEnabled(LogLevel.Debug))
        {
            _statLogged = now;
            var s = TakeStatistics();
            LogStatistics(s.Frames / s.Elapsed.TotalSeconds, s.NewImages / s.Elapsed.TotalSeconds, s.AverageLatency.TotalMilliseconds, s.MaxLatency.TotalMilliseconds, s.AverageWork.TotalMilliseconds);
        }
    }

    /// <summary>Resizes the frame to the capture layout and clears it once per layout (multi-monitor gaps stay black).</summary>
    private void PrepareFrame(Frame frame)
    {
        var (width, height) = _frameSize;
        if (frame.Width != width || frame.Height != height)
        {
            frame.Resize(width, height);
            _clearedFrame = null;
        }

        if (!ReferenceEquals(frame, _clearedFrame))
        {
            frame.Pixels.Clear();
            _clearedFrame = frame;
        }
    }

    private bool TryStart(CaptureSettings settings)
    {
        try
        {
            InputDesktop.MakeThreadDpiAware();
            var outputs = MonitorEnumerator.Enumerate();
            _monitors = [.. outputs.Select(o => o.Info)];
            var lastPath = string.Equals(_captured.Device, settings.Device, StringComparison.Ordinal) ? _captured.Path : null;
            var selected = MonitorEnumerator.SelectFor(outputs, settings.Device, lastPath, out var match);
            if (selected.Count == 0)
            {
                var available = string.Join(", ", outputs.Select(o => o.Info.Id).Concat(MonitorEnumerator.MultiMonitorIds(outputs)));
                return Retry(TimeSpan.FromSeconds(5), $"monitor '{settings.Device}' is not available (found: {(available.Length == 0 ? "none" : available)})");
            }

            if (MonitorEnumerator.PathToRemember(selected, match) is { } path)
            {
                _captured = (settings.Device, path);
            }

            _desktop.AttachCurrentThread();
            Open(settings, selected);
            LogStarted(_device, _frameSize.Width, _frameSize.Height, settings.FramesPerSecond);

            // once per fallback, not on every restart of the duplication (each display change, each lock screen retry)
            var fallback = match == MonitorMatch.Setting ? null : $"{settings.Device}\n{selected[0].Info.Id}\n{match}";
            if (fallback is not null && fallback != _loggedFallback)
            {
                LogFallback(settings.Device, selected[0].Info.Id, match == MonitorMatch.Renamed ? "the same monitor under a new name" : "the only connected monitor");
            }

            _loggedFallback = fallback;
            foreach (var d in _displays)
            {
                var pipeline = d.UsesGpu ? "GPU" : "CPU";
                var format = d.Format.ToString();
                LogDisplay(d.Monitor.Id, d.Monitor.FriendlyName, d.Monitor.DevicePath, d.Monitor.Hdr, format, d.WideGamut, d.AppliesLut, settings.ToneMapping, pipeline, d.Region.Width, d.Region.Height, d.Region.Divide);
                if (d.IsRotated)
                {
                    LogRotated(d.Monitor.Id, d.Rotation.ToString());
                }
            }

            _lastProblem = null;
            return true;
        }
        catch (CaptureException ex)
        {
            Teardown();

            // The secure desktop (UAC, lock screen) refuses duplication until the user is back.
            return ex.HResult == E.E_ACCESSDENIED
                ? Retry(TimeSpan.FromSeconds(1), "the desktop is not accessible (lock screen or UAC prompt)")
                : Retry(TimeSpan.FromSeconds(5), ex.Message);
        }
    }

    private bool Retry(TimeSpan delay, string problem)
    {
        _retryAt = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        if (problem != _lastProblem)
        {
            _lastProblem = problem;
            LogRetry(problem, delay.TotalSeconds);
        }

        return false;
    }

    private void Open(CaptureSettings settings, List<OutputLocation> selected)
    {
        IDXGIFactory1* factory = null;
        IDXGIAdapter1* adapter = null;
        try
        {
            Com.Check(CreateDXGIFactory1(__uuidof<IDXGIFactory1>(), (void**)&factory), "CreateDXGIFactory1");
            Com.Check(factory->EnumAdapters1(selected[0].Adapter, &adapter), "IDXGIFactory1::EnumAdapters1");
            _gpu = GpuDevice.Create(adapter);
            if (settings.Hardware)
            {
                try
                {
                    _gpu.CreateShaders();
                }
                catch (CaptureException ex)
                {
                    LogShadersFailed(ex);
                }
            }

            // Left to right as enumerated (sorted ids), then HyperHDR's reorder permutation.
            var order = DisplayOrder.Permute(selected.Count, settings.ReorderDisplays);
            foreach (var index in order)
            {
                _displays.Add(OpenOutput(adapter, selected[index], settings));
            }
        }
        finally
        {
            Com.Release(ref adapter);
            Com.Release(ref factory);
        }

        _offsets = new int[_displays.Count];
        int width = 0, height = 0;
        for (var i = 0; i < _displays.Count; i++)
        {
            _offsets[i] = width;
            width += _displays[i].Region.TargetWidth;
            height = Math.Max(height, _displays[i].Region.TargetHeight);
        }

        _frameSize = (width, height);
        _needsLut = _displays.Exists(d => d.AppliesLut);
        _device = selected.Count > 1 ? MonitorEnumerator.MultiMonitorPrefix + selected[0].Info.Adapter : selected[0].Info.Id;
        SetHdrStatus(new CaptureHdrStatus(
            settings.ToneMapping,
            Capturing: true,
            MonitorHdr: _displays.Exists(d => d.Monitor.Hdr),
            ToneMapped: _displays.Exists(d => d.WideGamut || d.AppliesLut)));
    }

    private void SetHdrStatus(CaptureHdrStatus status)
    {
        if (status == _hdrStatus)
        {
            return;
        }

        _hdrStatus = status;
        HdrStatusChanged?.Invoke(status);
    }

    private DisplayCapture OpenOutput(IDXGIAdapter1* adapter, OutputLocation location, CaptureSettings settings)
    {
        IDXGIOutput* output = null;
        IDXGIOutput6* output6 = null;
        try
        {
            Com.Check(adapter->EnumOutputs(location.Output, &output), "IDXGIAdapter1::EnumOutputs");
            Com.Check(output->QueryInterface(__uuidof<IDXGIOutput6>(), (void**)&output6), "QueryInterface(IDXGIOutput6)");
            DXGI_OUTPUT_DESC1 desc;
            Com.Check(output6->GetDesc1(&desc), "IDXGIOutput6::GetDesc1");
            if (!location.Info.Id.StartsWith(new string(&desc.DeviceName.e0) + "|", StringComparison.Ordinal))
            {
                // The outputs changed between enumeration and opening.
                throw new CaptureException("Opening " + location.Info.Id, DXGI.DXGI_ERROR_NOT_FOUND);
            }

            return DisplayCapture.Open(_gpu!, output6, location.Info, settings);
        }
        finally
        {
            Com.Release(ref output6);
            Com.Release(ref output);
        }
    }

    private void Teardown()
    {
        foreach (var display in _displays)
        {
            display.Dispose();
        }

        _displays.Clear();
        _gpu?.Dispose();
        _gpu = null;
        _device = "none";
        if (!_disposed)
        {
            SetHdrStatus(CaptureHdrStatus.Idle((_active ?? _pending).ToneMapping));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen capture started: {Device}, {Width}x{Height} frames at up to {Fps} fps")]
    private partial void LogStarted(string device, int width, int height, int fps);

    [LoggerMessage(Level = LogLevel.Information, Message = "Monitor {Id} ({Name}, {Path}): Windows HDR {Hdr}, duplicated as {Format} (HDR conversion: {WideGamut}, tone mapping table: {Lut}, mode {Mode}), {Pipeline} path; area {Width}x{Height} scaled down by {Divide}")]
    private partial void LogDisplay(string id, string name, string path, bool hdr, string format, bool wideGamut, bool lut, ToneMappingMode mode, string pipeline, int width, int height, int divide);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Monitor '{Device}' isn't connected under that name; capturing {Id}, {Reason}")]
    private partial void LogFallback(string device, string id, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Monitor {Id} is rotated ({Rotation}); it is captured in its unrotated (scanout) orientation")]
    private partial void LogRotated(string id, string rotation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen capture unavailable: {Problem}; retrying every {Seconds:0.#} s")]
    private partial void LogRetry(string problem, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen capture of {Id} was interrupted (display mode, HDR or desktop change; device status 0x{Reason:X8}); restarting")]
    private partial void LogLost(string id, int reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Screen capture failed")]
    private partial void LogCaptureFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create the scaling shaders; using the CPU path")]
    private partial void LogShadersFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list the monitors")]
    private partial void LogEnumerationFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Capture: {Fps:0.0} fps ({NewFps:0.0} new images/s), latency avg {Latency:0.0} ms max {MaxLatency:0.0} ms, work {Work:0.00} ms per frame")]
    private partial void LogStatistics(double fps, double newFps, double latency, double maxLatency, double work);
}
