using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Platform.Windows.Desktop;

/// <summary>
/// The thread that owns every window of the app: a single-threaded apartment with a Win32 message loop (WebView2 and
/// the tray need one), a hidden top-level window that receives the broadcasts (power, session end, taskbar restart),
/// and a <see cref="SynchronizationContext"/> so <c>await</c> continues on it. Work from other threads arrives through
/// <see cref="Post"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed unsafe partial class UiThread : IDisposable
{
    private const uint InvokeMessage = WM.WM_APP + 1;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Func<uint, WPARAM, LPARAM, LRESULT?>> _handlers = [];
    private NativeWindow? _window;
    private volatile bool _stopped;

    public UiThread(ILogger<UiThread> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _thread = new Thread(Run) { Name = "Backbloom UI", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.Wait();
    }

    /// <summary>The hidden message window (owner of the tray icon and the notification registrations).</summary>
    public HWND MessageWindow => _window?.Handle ?? HWND.NULL;

    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Runs <paramref name="action"/> on the UI thread (exceptions are logged).</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_stopped)
        {
            return;
        }

        _queue.Enqueue(action);
        PostMessageW(MessageWindow, InvokeMessage, default, default);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it (at most <paramref name="timeout"/>).</summary>
    public bool Invoke(Action action, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsCurrent)
        {
            action();
            return true;
        }

        using var done = new ManualResetEventSlim();
        Post(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.Set();
            }
        });
        return done.Wait(timeout);
    }

    /// <summary>
    /// Adds a handler for messages of the hidden window; it returns a result to stop the default handling. Called on
    /// the UI thread.
    /// </summary>
    public void AddMessageHandler(Func<uint, WPARAM, LPARAM, LRESULT?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_handlers)
        {
            _handlers.Add(handler);
        }
    }

    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }

        Post(() =>
        {
            _window?.Dispose();
            PostQuitMessage(0);
        });
        _stopped = true;
        if (!IsCurrent)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    internal void LogHandlerFailed(Exception exception) => LogFailed(exception);

    private void Run()
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(this));
        try
        {
            _window = new NativeWindow(this, "BackbloomMessageWindow", "Backbloom", 0, 0, OnMessage);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            LogFailed(ex);
            _ready.SetResult();
            return;
        }

        _ready.SetResult();
        MSG msg;
        while (GetMessageW(&msg, HWND.NULL, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    private LRESULT? OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == InvokeMessage)
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogFailed(ex);
                }
            }

            return 0;
        }

        Func<uint, WPARAM, LPARAM, LRESULT?>[] handlers;
        lock (_handlers)
        {
            handlers = [.. _handlers];
        }

        foreach (var handler in handlers)
        {
            if (handler(message, wParam, lParam) is { } result)
            {
                return result;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "UI thread: a window message handler failed")]
    private partial void LogFailed(Exception exception);

    private sealed class UiSynchronizationContext(UiThread thread) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => thread.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state) => thread.Invoke(() => d(state), Timeout.InfiniteTimeSpan);

        public override SynchronizationContext CreateCopy() => this;
    }
}
