using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Overglow.App;

/// <summary>
/// Makes every way the process ends switch the LEDs off first. Quit (tray, <c>--quit</c>) and Ctrl+C stop the host
/// normally; the engine then switches the controllers off (black frame, then closed). Where Windows ends the process
/// as soon as a notification returns — the session ends (<c>WM_ENDSESSION</c>), or the console window is closed or
/// the system shuts down (SIGHUP / SIGTERM, i.e. CTRL_CLOSE / CTRL_SHUTDOWN) — the notification stops the host and
/// waits until the LEDs are off (at most <see cref="Timeout"/>).
/// </summary>
public sealed partial class GracefulShutdown(IHostApplicationLifetime lifetime, ILogger<GracefulShutdown> logger) : IDisposable
{
    /// <summary>Longest wait for the LEDs (Windows gives a console handler 5 s and a session end a few more).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4.5);

    private readonly ManualResetEventSlim _lightsOff = new();
    private readonly List<PosixSignalRegistration> _signals = [];

    /// <summary>True once the controllers are switched off.</summary>
    public bool LightsOff => _lightsOff.IsSet;

    /// <summary>Called by the engine's hosted service after the controllers are off.</summary>
    public void SignalLightsOff() => _lightsOff.Set();

    /// <summary>Stops the host and waits for the LEDs; returns false on timeout.</summary>
    public bool StopAndWait(string reason)
    {
        LogStopping(reason);
        lifetime.StopApplication();
        var off = _lightsOff.Wait(Timeout);
        if (!off)
        {
            LogTimedOut(Timeout.TotalSeconds);
        }

        return off;
    }

    /// <summary>Registers the console close and shutdown signals (only for a process with a console).</summary>
    public void HandleConsoleSignals()
    {
        foreach (var signal in new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM })
        {
            _signals.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                StopAndWait(context.Signal.ToString());
            }));
        }
    }

    public void Dispose()
    {
        foreach (var signal in _signals)
        {
            signal.Dispose();
        }

        _signals.Clear();
        _lightsOff.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutting down ({Reason}): switching the LEDs off")]
    private partial void LogStopping(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The LEDs weren't off after {Seconds} s")]
    private partial void LogTimedOut(double seconds);
}
