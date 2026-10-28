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
/// dropped, never queued), refreshes, overlays the identify blink and switches the device off again. A blink also runs
/// while the runner is switched off: the device opens for it and closes again afterwards.
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
    private readonly TimeProvider _time;
    private readonly Lock _blinkLock = new();
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
    // The blink, written under _blinkLock so a new blink never races with the end of the previous one (the index is
    // also read without it to decide whether to tick); _redraw asks for the current frame once after a stopped blink.
    private int _blinkIndex = -1;
    private long _blinkStarted;
    private bool _blinkHold;
    private int _redraw;
    private long _retryAt;
    private long _lastWrite;
    private int _failedAttempts;
    private long _offAt;
    private bool _dark;
    private TimeSpan? _refresh;
    private bool _started;

    /// <param name="time">Clock of the identify blink (tests); the system clock by default.</param>
    public LedDeviceRunner(ILedDriver driver, LedDeviceRunnerOptions options, ILogger logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(options.LedCount);
        _driver = driver;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
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

    /// <summary>What the controller reports about itself, when its driver reads that (<see cref="ITelemetryDriver"/>).</summary>
    public ControllerTelemetry? Telemetry => (_driver as ITelemetryDriver)?.Telemetry;

    /// <summary>
    /// True while an identify blink runs. A blink is over when its time is up, also while the device couldn't open to
    /// show it (it then stops trying for the blink).
    /// </summary>
    public bool Blinking
    {
        get
        {
            if (Volatile.Read(ref _blinkIndex) < 0)
            {
                return false;
            }

            lock (_blinkLock)
            {
                return _blinkIndex >= 0 && BlinkPattern.ColorAt(_time.GetElapsedTime(_blinkStarted), _blinkHold) is not null;
            }
        }
    }

    // The device is wanted: switched on, or showing a blink.
    private bool Active => _enabled || Blinking;

    // Switched off with a delay that isn't over: the device stays open and shows black.
    private bool Lingering => !_enabled && Stopwatch.GetTimestamp() < Volatile.Read(ref _offAt);

    // A blink was set and not yet taken off the LEDs (ApplyBlink clears it once it is over).
    private bool BlinkPending => Volatile.Read(ref _blinkIndex) >= 0;

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

    /// <summary>
    /// Switches the device on (open, then stream) or off (black frame, then close). With an <paramref name="offDelay"/>
    /// an open device first stays open and shows black for that long, so a short gap in the picture (a capture that
    /// restarts) doesn't switch the controller off and reconnect it; switching on meanwhile goes on streaming. A later
    /// switch-off without a delay closes at once.
    /// </summary>
    public void SetEnabled(bool enabled, TimeSpan offDelay = default)
    {
        if (enabled && !_enabled)
        {
            _retryRequested = true;
        }

        // switching on leaves the deadline alone (lingering needs the device off, and switching off from on sets it
        // anew), so the device thread can't see it cleared before it sees the device on and close it in between
        if (!enabled && (_enabled || offDelay <= TimeSpan.Zero))
        {
            Volatile.Write(ref _offAt, offDelay > TimeSpan.Zero ? Stopwatch.GetTimestamp() + (long)(offDelay.TotalSeconds * Stopwatch.Frequency) : 0);
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
            // a drop means the controller can't keep up: while it opens or waits to retry, nothing is taken anyway
            if (_hasPending && _machine.State == DeviceState.Ready)
            {
                Stats.OnDropped();
            }

            _hasPending = true;
            _pendingSince = Stopwatch.GetTimestamp();
        }

        Signal();
    }

    /// <summary>
    /// Blinks one LED red, green and blue (4.5 s) while every other LED is black; <paramref name="hold"/> keeps it
    /// cycling until <see cref="StopIdentify"/>, the next blink or <see cref="BlinkPattern.HoldTimeout"/>. A new blink
    /// replaces the running one and starts over at red. While the runner is switched off, the device opens for the
    /// blink and closes again after it; a device that gave up opening gets another attempt.
    /// </summary>
    public void Identify(int ledIndex, bool hold = false)
    {
        if ((uint)ledIndex >= (uint)_options.LedCount || _disposing)
        {
            return;
        }

        lock (_blinkLock)
        {
            _blinkStarted = _time.GetTimestamp();
            _blinkHold = hold;
            Volatile.Write(ref _blinkIndex, ledIndex);
        }

        if (_machine.State == DeviceState.Failed)
        {
            _retryRequested = true;
        }

        Signal();
    }

    /// <summary>Ends the blink: the LEDs show the current frame again (or, switched off, the device closes).</summary>
    public void StopIdentify()
    {
        lock (_blinkLock)
        {
            if (_blinkIndex < 0)
            {
                return;
            }

            Volatile.Write(ref _blinkIndex, -1);
        }

        Interlocked.Exchange(ref _redraw, 1);
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

                    if (Active)
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
                    if (!Active || _disposing)
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
                    if (_disposing || !Active)
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
                        // a blink that made the device open while switched off ends by itself: look again then
                        _signal.WaitOne(_enabled ? Timeout.InfiniteTimeSpan : s_blinkTick);
                    }

                    break;

                case DeviceState.Ready:
                    if (_disposing || (!Active && !Lingering))
                    {
                        Close();
                    }
                    else if (!Active)
                    {
                        Linger();
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
                LogRetry(_failedAttempts, delay.Value, ex.Message);
                Volatile.Write(ref _retryAt, Stopwatch.GetTimestamp() + (long)(delay.Value.TotalSeconds * Stopwatch.Frequency));
                MoveTo(DeviceState.WaitingToRetry);
            }
        }
    }

    // Every attempt is logged until the retries slow down; that is said once, the slow attempts only at debug level.
    private void LogRetry(int failedAttempts, TimeSpan delay, string reason)
    {
        var policy = _driver.RetryPolicy;
        if (failedAttempts <= policy.MaxRetries)
        {
            LogOpenFailed(_options.Name, failedAttempts, delay.TotalSeconds, reason);
        }
        else if (policy.SlowsDownAfter(failedAttempts))
        {
            LogSlowingDown(_options.Name, failedAttempts, delay.TotalSeconds, reason);
        }
        else
        {
            LogStillFailing(_options.Name, failedAttempts, delay.TotalSeconds, reason);
        }
    }

    // Switched off with a delay: black (sent again when a refresh is due) until the delay is over or it's switched on.
    private void Linger()
    {
        var refreshDue = _refresh is { } refresh && Stopwatch.GetElapsedTime(_lastWrite) >= refresh;
        if (!_dark || refreshDue)
        {
            _dark = true;
            _output.AsSpan().Clear();
            try
            {
                Send(_output);
            }
            catch (DeviceLostException ex)
            {
                Lost(ex);
                return;
            }
        }

        var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Volatile.Read(ref _offAt));
        if (left > TimeSpan.Zero)
        {
            _signal.WaitOne(_refresh is { } r && r < left ? r : left);
        }
    }

    private void Stream()
    {
        _dark = false;
        var blinked = BlinkPending;
        var timeout = blinked ? s_blinkTick : _refresh ?? Timeout.InfiniteTimeSpan;
        _signal.WaitOne(timeout);
        if (!Active || _disposing)
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

        // a blink that ended (blinked) or was stopped (redraw) meanwhile gives way to the current frame
        var redraw = Interlocked.Exchange(ref _redraw, 0) != 0;
        var refreshDue = _refresh is { } refresh && Stopwatch.GetElapsedTime(_lastWrite) >= refresh;
        if (hasNew || blinked || BlinkPending || redraw || refreshDue)
        {
            _current.CopyTo(_output, 0);
            if (!ApplyBlink() && !_enabled)
            {
                // the blink is over while switched off: close (black) rather than show an old frame
                return;
            }

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

    // Overlays the blink on _output; false when there is none (any more).
    private bool ApplyBlink()
    {
        lock (_blinkLock)
        {
            var index = _blinkIndex;
            if (index < 0)
            {
                return false;
            }

            if (BlinkPattern.Apply(_output, index, _time.GetElapsedTime(_blinkStarted), _blinkHold))
            {
                return true;
            }

            Volatile.Write(ref _blinkIndex, -1);
            return false;
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

        Stats.OnWriteTime(Stopwatch.GetTimestamp() - _lastWrite);
        if (written)
        {
            Stats.OnWritten();
            MeasureLoad(colors);
        }
        else
        {
            Stats.OnWriteError();
        }

        return written;
    }

    // What the LEDs draw, before the controller's own brightness: the means of the channels (0..1, shares of full
    // white) and the brightest LED. One pass over the colors, on the device thread.
    private void MeasureLoad(ReadOnlySpan<Vector3> colors)
    {
        if (colors.IsEmpty)
        {
            return;
        }

        var sum = Vector3.Zero;
        var brightest = 0f;
        foreach (var color in colors)
        {
            var c = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
            sum += c;
            brightest = MathF.Max(brightest, c.X + c.Y + c.Z);
        }

        Stats.OnSent(sum / colors.Length, brightest / 3.0);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name}: couldn't open the device {Attempts} times, trying every {Seconds:0.#} s from now on: {Reason}")]
    private partial void LogSlowingDown(string name, int attempts, double seconds, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Name}: couldn't open the device (attempt {Attempt}), retrying in {Seconds:0.#} s: {Reason}")]
    private partial void LogStillFailing(string name, int attempt, double seconds, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name}: device switched off")]
    private partial void LogClosed(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name}: couldn't switch the device off cleanly: {Reason}")]
    private partial void LogCloseFailed(string name, string reason);
}
