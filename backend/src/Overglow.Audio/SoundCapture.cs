using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Overglow.Core.Audio;

namespace Overglow.Audio;

/// <summary>The <c>soundEffect</c> settings.</summary>
/// <param name="Enable">Sound capture for the music effects is allowed.</param>
/// <param name="Device">Device id (<see cref="AudioDevice.Id"/>), <see cref="DefaultDevice"/>, or a device name from HyperHDR.</param>
/// <param name="Smoothing">Two interpolated steps between sound blocks (<c>enable_smoothing</c>).</param>
public sealed record SoundSettings(bool Enable, string Device, bool Smoothing)
{
    public const string SectionName = "soundEffect";

    /// <summary>What the default output device plays (the schema default; HyperHDR had no such device).</summary>
    public const string DefaultDevice = "default";

    public static SoundSettings Disabled { get; } = new(false, DefaultDevice, true);

    /// <summary>Reads the section with the C++ fallbacks (<c>toBool(true)</c>, empty device).</summary>
    public static SoundSettings FromJson(JsonNode? section)
    {
        var o = section as JsonObject;
        return new SoundSettings(
            Flag(o, "enable"),
            o?["device"] is JsonValue d && d.TryGetValue(out string? device) ? device.Trim() : string.Empty,
            Flag(o, "enable_smoothing"));

        static bool Flag(JsonObject? o, string name) => o?[name] is not JsonValue v || !v.TryGetValue(out bool b) || b;
    }

    /// <summary>Capture can run: enabled and a device chosen (C++ <c>_isActive</c>).</summary>
    public bool Active => Enable && Device.Length > 0;
}

/// <summary>What <c>serverinfo.sound</c> reports.</summary>
/// <param name="Active">Capture is configured and its device works (or no music effect needed it yet).</param>
/// <param name="Running">A music effect runs and the device delivers sound.</param>
public sealed record SoundInfo(bool Active, bool Running, string Device, IReadOnlyList<AudioDevice> Devices);

/// <summary>
/// Music mode's sound capture: runs the platform capture of the configured device while at least one music effect
/// has it open, and feeds <see cref="Analysis"/>. Port of <c>SoundCapture</c> (sources/base/SoundCapture.cpp, MIT,
/// awawa-dev) without the analysis (in <see cref="MusicAnalysis"/>); settings changes apply at once (HyperHDR only
/// used them at the next start). Thread-safe.
/// </summary>
public sealed partial class SoundCapture : IDisposable, IAudioCaptureObserver
{
    private const int SilentBlocksBeforeWarning = 20;
    private static readonly TimeSpan s_devicesCacheTime = TimeSpan.FromSeconds(2);

    private readonly IAudioCapture? _platform;
    private readonly ILogger _logger;
    private readonly AudioBlockWriter _writer;
    private readonly Lock _lock = new();
    private readonly HashSet<int> _handles = [];
    private SoundSettings _settings = SoundSettings.Disabled;
    private IDisposable? _session;
    private string? _failure;
    private int _nextHandle;
    private IReadOnlyList<AudioDevice> _devices = [];
    private DateTime _devicesRead;
    private int _silentRun;
    private volatile bool _soundReported;
    private volatile bool _silenceReported;
    private bool _disposed;

    /// <param name="platform">The platform capture; null where there is none (music effects then stay dark).</param>
    public SoundCapture(IAudioCapture? platform, ILogger<SoundCapture> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _platform = platform;
        _logger = logger;
        _writer = new AudioBlockWriter(Analysis);
        _writer.BlockAnalyzed += OnBlockAnalyzed;
    }

    public MusicAnalysis Analysis { get; } = new();

    /// <summary>Blocks analyzed since start (for diagnostics and tests).</summary>
    public long Blocks => _writer.Blocks;

    /// <summary>Raised after the capture state changed (started, stopped, failed, settings).</summary>
    public event Action? Changed;

    public void Configure(SoundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IDisposable? stopped = null;
        lock (_lock)
        {
            var previous = _settings;
            _settings = settings;
            Analysis.Smoothing = settings.Smoothing;
            if (previous.Active != settings.Active || !string.Equals(previous.Device, settings.Device, StringComparison.Ordinal))
            {
                _failure = null;
                stopped = DetachSession();
            }

            LogConfigured(settings.Active ? settings.Device : "(disabled)", settings.Smoothing);
        }

        // the old session ends before the new one starts (they share the writer); never under the lock, which the
        // capture thread takes to report
        stopped?.Dispose();
        lock (_lock)
        {
            StartIfNeeded();
        }

        Changed?.Invoke();
    }

    /// <summary>A music effect starts: the capture runs while any handle is open (C++ <c>open</c>).</summary>
    public int Open()
    {
        int handle;
        lock (_lock)
        {
            handle = ++_nextHandle;
            _handles.Add(handle);
            if (!_settings.Active)
            {
                LogNotConfigured();
            }

            StartIfNeeded();
        }

        Changed?.Invoke();
        return handle;
    }

    /// <summary>A music effect ended; the last one stops the capture (C++ <c>close</c>).</summary>
    public void Close(int handle)
    {
        IDisposable? stopped;
        lock (_lock)
        {
            if (!_handles.Remove(handle) || _handles.Count > 0)
            {
                return;
            }

            stopped = DetachSession();
        }

        stopped?.Dispose();
        Changed?.Invoke();
    }

    public SoundInfo GetInfo()
    {
        lock (_lock)
        {
            if (_platform is not null && DateTime.UtcNow - _devicesRead > s_devicesCacheTime)
            {
                try
                {
                    _devices = _platform.GetDevices();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogEnumerationFailed(ex);
                    _devices = [];
                }

                _devicesRead = DateTime.UtcNow;
            }

            return new SoundInfo(_settings.Active && _platform is not null && _failure is null, _session is not null && _soundReported, _settings.Device, _devices);
        }
    }

    public void Dispose()
    {
        IDisposable? stopped;
        lock (_lock)
        {
            _disposed = true;
            stopped = DetachSession();
        }

        stopped?.Dispose();
    }

    void IAudioCaptureObserver.Opened(string deviceName, AudioFormat format)
    {
        lock (_lock)
        {
            _failure = null;
        }

        LogOpened(deviceName, format.SampleRate, format.Channels, format.SampleFormat);
        Changed?.Invoke();
    }

    void IAudioCaptureObserver.Failed(string reason)
    {
        bool first;
        lock (_lock)
        {
            first = _failure != reason;
            _failure = reason;
        }

        if (first)
        {
            LogFailed(reason);
            Changed?.Invoke();
        }
    }

    // Runs under _lock.
    private void StartIfNeeded()
    {
        if (_disposed || _session is not null || _handles.Count == 0 || !_settings.Active)
        {
            return;
        }

        if (_platform is null)
        {
            LogNoPlatform();
            return;
        }

        _silentRun = 0;
        _soundReported = _silenceReported = false;
        Analysis.Start();
        _session = _platform.Start(_settings.Device, _writer, this);
        LogStarted(_settings.Device, _platform.Name);
    }

    // Runs under _lock; the caller disposes the session after releasing it.
    private IDisposable? DetachSession()
    {
        var session = _session;
        if (session is null)
        {
            return null;
        }

        Analysis.Stop();
        _session = null;
        _soundReported = false;
        LogStopped();
        return session;
    }

    // The C++ "silent" warning after 20 silent blocks in a row and the "sound detected" note, each once per phase.
    private void OnBlockAnalyzed(bool silent)
    {
        if (silent)
        {
            if (++_silentRun > SilentBlocksBeforeWarning && !_silenceReported)
            {
                _silenceReported = true;
                _soundReported = false;
                LogSilent();
                Changed?.Invoke();
            }
        }
        else
        {
            _silentRun = 0;
            if (!_soundReported)
            {
                _soundReported = true;
                _silenceReported = false;
                LogSound();
                Changed?.Invoke();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sound capture: device {Device}, smoothing {Smoothing}")]
    private partial void LogConfigured(string device, bool smoothing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A music effect started, but sound capture is disabled (soundEffect settings)")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sound capture isn't available on this system")]
    private partial void LogNoPlatform();

    [LoggerMessage(Level = LogLevel.Information, Message = "Sound capture starts: device {Device} ({Platform})")]
    private partial void LogStarted(string device, string platform);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sound capture stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Information, Message = "Sound device opened: {Device}, {Rate} Hz, {Channels} channels, {Format}")]
    private partial void LogOpened(string device, int rate, int channels, AudioSampleFormat format);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sound capture failed: {Reason}; retrying")]
    private partial void LogFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sound devices couldn't be listed")]
    private partial void LogEnumerationFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio stream: sound detected")]
    private partial void LogSound();

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio stream: audio data captured, but it is silent")]
    private partial void LogSilent();
}
