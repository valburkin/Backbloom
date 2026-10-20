using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Backbloom.Engine.Platform;

namespace Backbloom.Platform.Windows;

/// <summary>
/// <see cref="IClock"/> whose waits use a high-resolution waitable timer (Windows 10 1803+): the smoothing tick wakes
/// within about half a millisecond of its deadline without raising the system-wide timer resolution
/// (<c>timeBeginPeriod</c>, which Qt's precise timers used). Falls back to <see cref="SystemClock"/>'s sleep where
/// the timer can't be created.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsClock : SystemClock, IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;
    private const long TicksPerMillisecond = 10_000;

    // one timer per waiting thread (the smoothing thread), created on its first wait
    [ThreadStatic]
    private static nint s_threadTimer;

    private readonly Lock _lock = new();
    private readonly List<nint> _timers = [];
    private bool _disposed;

    public override void SleepUntil(long dueMilliseconds)
    {
        var wait = dueMilliseconds - NowMilliseconds;
        if (wait <= 0)
        {
            return;
        }

        var timer = TimerForThisThread();
        if (timer == 0)
        {
            base.SleepUntil(dueMilliseconds);
            return;
        }

        // negative: relative time in 100 ns units
        var due = -wait * TicksPerMillisecond;
        if (!SetWaitableTimer(timer, in due, 0, 0, 0, false) || WaitForSingleObject(timer, Infinite) != 0)
        {
            base.SleepUntil(dueMilliseconds);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var timer in _timers)
            {
                CloseHandle(timer);
            }

            _timers.Clear();
        }
    }

    private nint TimerForThisThread()
    {
        if (s_threadTimer != 0)
        {
            return s_threadTimer;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return 0;
            }

            var timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (timer != 0)
            {
                _timers.Add(timer);
            }

            s_threadTimer = timer;
            return timer;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completionRoutine, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
