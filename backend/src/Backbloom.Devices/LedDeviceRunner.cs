using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Backbloom.Core.Color;

namespace Backbloom.Devices;

/// <summary>Per-device settings that are independent of the protocol.</summary>
/// <param name="Name">Name used in logs and thread names.</param>
/// <param name="LedCount">LEDs on this controller.</param>
/// <param name="ColorOrder">Byte order the controller expects.</param>
/// <param name="RefreshInterval">Resend the last frame when nothing new arrived for this long (null = never).</param>
public sealed record LedDeviceRunnerOptions(string Name, int LedCount, ColorOrder ColorOrder = ColorOrder.Rgb, TimeSpan? RefreshInterval = null);

/// <summary>
/// Runs one LED device on its own thread: opens it with retries, sends the newest frame (older unsent frames are
/// dropped, never queued), refreshes, overlays the identify blink and switches the device off again.
/// </summary>
/// <remarks>
/// The pipeline thread only copies colors into a preallocated buffer in <see cref="Submit"/>, so a slow or
/// unreachable controller can never stall the frame loop or the other controllers. No per-frame allocations.
/// </remarks>
public sealed partial class LedDeviceRunner : IDisposable
{
    private static readonly TimeSpan s_blinkTick = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(3);

    private readonly ILedDriver _driver;
    private readonly LedDeviceRunnerOptions _options;
    private readonly ILogger _logger;
    private readonly DeviceStateMachine _machine = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _frameLock = new();
    private readonly Thread _thread;

    private Vector3[] _pending;
    private Vector3[] _current;
    private readonly Vector3[] _output;
    private readonly byte[] _bytes;
    private bool _hasPending;
    private long _pendingSince;

    private volatile bool _enabled;
    private volatile bool _disposing;
    private volatile bool _retryRequested;
    private int _blinkIndex = -1;
    private long _blinkStarted;
    private long _retryAt;
    private long _lastWrite;
    private int _failedAttempts;
    private TimeSpan? _refresh;
    private bool _started;

    public LedDeviceRunner(ILedDriver driver, LedDeviceRunnerOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(options.LedCount);
        _driver = driver;
        _options = options;
        _logger = logger;
        _pending = new Vector3[options.LedCount];
        _current = new Vector3[options.LedCount];
        _output = new Vector3[options.LedCount];
        _bytes = new byte[options.LedCount * 3];
        _thread = new Thread(Run) { IsBackground = true, Name = $"LED device {options.Name}" };
    }

    /// <summary>Raised on the device thread after every state change.</summary>
    public event Action<LedDeviceRunner, DeviceState>? StateChanged;

    public string Name => _options.Name;

    public string Type => _driver.Type;

    public int LedCount => _options.LedCount;

    public string? Status => _driver.Status;

    public DeviceState State => _machine.State;

    public FrameStats Stats { get; } = new();

    /// <summary>Starts the device thread. The device opens once it is enabled.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposing, this);
        if (!_started)
        {
            _started = true;
            _thread.Start();
        }
    }

    /// <summary>Switches the device on (open, then stream) or off (black frame, then close).</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled && !_enabled)
        {
            _retryRequested = true;
        }

        _enabled = enabled;
        Signal();
    }

    /// <summary>Hands the newest processed colors (0..1, RGB) to the device. Called from the pipeline thread.</summary>
    public void Submit(ReadOnlySpan<Vector3> colors)
    {
        Stats.OnSubmitted();
        if (!_enabled || _disposing)
        {
            return;
        }

        lock (_frameLock)
        {
            var n = Math.Min(colors.Length, _pending.Length);
            colors[..n].CopyTo(_pending);
            _pending.AsSpan(n).Clear();
            if (_hasPending)
            {
                Stats.OnDropped();
            }

            _hasPending = true;
            _pendingSince = Stopwatch.GetTimestamp();
        }

        Signal();
    }

    /// <summary>Blinks one LED red, green and blue (4.5 s) while every other LED is black.</summary>
    public void Identify(int ledIndex)
    {
        if ((uint)ledIndex >= (uint)_options.LedCount)
        {
            return;
        }

        Volatile.Write(ref _blinkStarted, Stopwatch.GetTimestamp());
        Volatile.Write(ref _blinkIndex, ledIndex);
        Signal();
    }

    public void Dispose()
    {
        if (_disposing)
        {
            return;
        }

        _disposing = true;
        _enabled = false;
        if (_machine.State == DeviceState.Opening)
        {
            // Nothing to switch off yet: abort the connection attempt instead of waiting for it.
            _lifetime.Cancel();
        }

        _signal.Set();
        if (_started && !_thread.Join(s_closeTimeout * 2))
        {
            _lifetime.Cancel();
            _thread.Join(s_closeTimeout);
        }

        _lifetime.Cancel();
        _driver.Dispose();
        _lifetime.Dispose();
        _signal.Dispose();
    }

    // The pipeline may still hand over a frame while the runner is being replaced; that frame is simply dropped.
    private void Signal()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Run()
    {
        while (true)
        {
            switch (_machine.State)
            {
                case DeviceState.Off:
                    if (_disposing)
                    {
                        return;
                    }

                    if (_enabled)
                    {
                        _retryRequested = false;
                        _failedAttempts = 0;
                        MoveTo(DeviceState.Opening);
                    }
                    else
                    {
                        _signal.WaitOne();
                    }

                    break;

                case DeviceState.Opening:
                    Open();
                    break;

                case DeviceState.WaitingToRetry:
                    if (!_enabled || _disposing)
                    {
                        MoveTo(DeviceState.Off);
                        break;
                    }

                    var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Volatile.Read(ref _retryAt));
                    if (wait <= TimeSpan.Zero)
                    {
                        MoveTo(DeviceState.Opening);
                    }
                    else
                    {
                        _signal.WaitOne(wait);
                    }

                    break;

                case DeviceState.Failed:
                    if (_disposing || !_enabled)
                    {
                        MoveTo(DeviceState.Off);
                    }
                    else if (_retryRequested)
                    {
                        _retryRequested = false;
                        _failedAttempts = 0;
                        MoveTo(DeviceState.Opening);
                    }
                    else
                    {
                        _signal.WaitOne();
                    }

                    break;

                case DeviceState.Ready:
                    if (!_enabled || _disposing)
                    {
                        Close();
                    }
                    else
                    {
                        Stream();
                    }

                    break;

                case DeviceState.Closing:
                default:
                    throw new UnreachableException($"Unexpected device state {_machine.State}.");
            }
        }
    }

    private void Open()
    {
        try
        {
            _driver.OpenAsync(_options.LedCount, _lifetime.Token).AsTask().GetAwaiter().GetResult();
            _failedAttempts = 0;
            _refresh = _driver.EffectiveRefreshInterval(_options.RefreshInterval);
            LogOpened(_options.Name, _driver.Type, _options.LedCount);
            MoveTo(DeviceState.Ready);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            MoveTo(DeviceState.Off);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _failedAttempts++;
            var delay = _driver.RetryPolicy.NextDelay(_failedAttempts);
            if (delay is null)
            {
                LogGaveUp(ex, _options.Name, _failedAttempts);
                MoveTo(DeviceState.Failed);
            }
            else
            {
                LogOpenFailed(_options.Name, _failedAttempts, delay.Value.TotalSeconds, ex.Message);
                Volatile.Write(ref _retryAt, Stopwatch.GetTimestamp() + (long)(delay.Value.TotalSeconds * Stopwatch.Frequency));
                MoveTo(DeviceState.WaitingToRetry);
            }
        }
    }

    private void Stream()
    {
        var blinking = Volatile.Read(ref _blinkIndex) >= 0;
        var timeout = blinking ? s_blinkTick : _refresh ?? Timeout.InfiniteTimeSpan;
        _signal.WaitOne(timeout);
        if (!_enabled || _disposing)
        {
            return;
        }

        var hasNew = false;
        long submittedAt = 0;
        lock (_frameLock)
        {
            if (_hasPending)
            {
                (_pending, _current) = (_current, _pending);
                _hasPending = false;
                hasNew = true;
                submittedAt = _pendingSince;
            }
        }

        var refreshDue = _refresh is { } refresh && Stopwatch.GetElapsedTime(_lastWrite) >= refresh;
        if (hasNew || blinking || refreshDue)
        {
            _current.CopyTo(_output, 0);
            ApplyBlink();
            try
            {
                if (Send(_output) && hasNew)
                {
                    Stats.OnLatency(Stopwatch.GetTimestamp() - submittedAt);
                }
            }
            catch (DeviceLostException ex)
            {
                Lost(ex);
            }
        }
    }

    /// <summary>The controller went away while streaming: reopen it with the retry policy (the driver resets itself in OpenAsync).</summary>
    private void Lost(DeviceLostException ex)
    {
        _failedAttempts = 1;
        var delay = _driver.RetryPolicy.NextDelay(_failedAttempts);
        if (delay is null)
        {
            LogGaveUp(ex, _options.Name, _failedAttempts);
            MoveTo(DeviceState.Failed);
            return;
        }

        LogLost(_options.Name, delay.Value.TotalSeconds, ex.Message);
        Volatile.Write(ref _retryAt, Stopwatch.GetTimestamp() + (long)(delay.Value.TotalSeconds * Stopwatch.Frequency));
        MoveTo(DeviceState.WaitingToRetry);
    }

    private void ApplyBlink()
    {
        var index = Volatile.Read(ref _blinkIndex);
        if (index < 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _blinkStarted));
        if (!BlinkPattern.Apply(_output, index, elapsed))
        {
            Interlocked.CompareExchange(ref _blinkIndex, -1, index);
        }
    }

    private bool Send(ReadOnlySpan<Vector3> colors)
    {
        _lastWrite = Stopwatch.GetTimestamp();
        bool written;
        if (_driver is IFloatColorDriver { WritesFloatColors: true } floatDriver)
        {
            written = floatDriver.WriteFloat(colors, _refresh ?? TimeSpan.Zero);
        }
        else
        {
            ColorQuantizer.ToBytes(colors, _options.ColorOrder, _bytes);
            written = _driver.Write(_bytes);
        }

        if (written)
        {
            Stats.OnWritten();
        }
        else
        {
            Stats.OnWriteError();
        }

        return written;
    }

    private void Close()
    {
        MoveTo(DeviceState.Closing);
        try
        {
            _output.AsSpan().Clear();
            Send(_output);
            using var timeout = new CancellationTokenSource(s_closeTimeout);
            _driver.CloseAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            LogClosed(_options.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCloseFailed(_options.Name, ex.Message);
        }

        MoveTo(DeviceState.Off);
    }

    private void MoveTo(DeviceState next)
    {
        _machine.MoveTo(next);
        StateChanged?.Invoke(this, next);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name}: {Type} device ready ({LedCount} LEDs)")]
    private partial void LogOpened(string name, string type, int ledCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name}: couldn't open the device (attempt {Attempt}), retrying in {Seconds:0.#} s: {Reason}")]
    private partial void LogOpenFailed(string name, int attempt, double seconds, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Name}: giving up after {Attempts} failed attempts to open the device")]
    private partial void LogGaveUp(Exception exception, string name, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name}: lost the device, reopening in {Seconds:0.#} s: {Reason}")]
    private partial void LogLost(string name, double seconds, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name}: device switched off")]
    private partial void LogClosed(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name}: couldn't switch the device off cleanly: {Reason}")]
    private partial void LogCloseFailed(string name, string reason);
}
