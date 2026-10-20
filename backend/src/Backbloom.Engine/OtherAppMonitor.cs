using Microsoft.Extensions.Logging;
using Backbloom.Engine.Platform;

namespace Backbloom.Engine;

/// <summary>Another ambient lighting program that drives the same kind of LED controllers.</summary>
/// <param name="Executable">Its process image name, e.g. "hyperhdr.exe".</param>
/// <param name="Name">What the UI calls it.</param>
public sealed record OtherLightingApp(string Executable, string Name);

/// <summary>
/// Notices when HyperHDR (or the older Hyperion) runs on this computer: two programs streaming to one LED controller
/// make it flicker, so the <see cref="LightingGate"/> holds Backbloom's output while one runs. Polls the process list
/// every <see cref="PollInterval"/> (one process snapshot, no handles kept open); the first check runs in
/// <see cref="Start"/>, before the lights start.
/// </summary>
public sealed partial class OtherAppMonitor : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IProcessList _processes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string[] _executables;
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private OtherLightingApp? _running;
    private bool _failed;
    private bool _disposed;

    public OtherAppMonitor(IProcessList processes, TimeProvider time, ILogger<OtherAppMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _processes = processes;
        _time = time;
        _logger = logger;
        _executables = [.. Known.Select(a => a.Executable)];
    }

    /// <summary>HyperHDR, and Hyperion (whose program HyperHDR's was forked from).</summary>
    public static IReadOnlyList<OtherLightingApp> Known { get; } =
    [
        new("hyperhdr.exe", "HyperHDR"),
        new("hyperiond.exe", "Hyperion"),
    ];

    /// <summary>Raised (on a timer thread) when another lighting app started or quit; null: none runs.</summary>
    public event Action<OtherLightingApp?>? Changed;

    /// <summary>The other lighting app that runs now, or null.</summary>
    public OtherLightingApp? Running
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    /// <summary>Checks once now, then every <see cref="PollInterval"/>.</summary>
    public void Start()
    {
        Check();
        lock (_lock)
        {
            if (_timer is null && !_disposed)
            {
                _timer = _time.CreateTimer(_ => Check(), null, PollInterval, PollInterval);
            }
        }
    }

    /// <summary>Looks at the process list now (also called by the timer).</summary>
    public void Check()
    {
        OtherLightingApp? previous;
        OtherLightingApp? found;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var executable = _processes.FindRunning(_executables);
                found = executable is null ? null : Known.First(a => string.Equals(a.Executable, executable, StringComparison.OrdinalIgnoreCase));
                _failed = false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!_failed)
                {
                    LogCheckFailed(ex.Message);
                }

                _failed = true;
                return;
            }

            previous = _running;
            if (previous == found)
            {
                return;
            }

            _running = found;
        }

        if (found is not null)
        {
            LogStarted(found.Name);
        }
        else
        {
            LogQuit(previous!.Name);
        }

        Changed?.Invoke(found);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{App} is running: Backbloom doesn't send to the LED controllers until it quits")]
    private partial void LogStarted(string app);

    [LoggerMessage(Level = LogLevel.Information, Message = "{App} quit")]
    private partial void LogQuit(string app);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the process list to look for HyperHDR: {Reason}")]
    private partial void LogCheckFailed(string reason);
}
