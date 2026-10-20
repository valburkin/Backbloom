using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Platform.Windows.Desktop;

/// <summary>Where and how a <see cref="NativeWindow"/> is created.</summary>
internal readonly record struct WindowSpec(string ClassName, string Title, uint Style, uint ExStyle, int X, int Y, int Width, int Height)
{
    public HICON Icon { get; init; }

    public HICON SmallIcon { get; init; }

    /// <summary>Background color (0x00BBGGRR) painted before the content draws; null: none.</summary>
    public uint? Background { get; init; }
}

/// <summary>
/// A Win32 window whose messages go to a managed handler (null result: default handling). Created and used on the
/// <see cref="UiThread"/> only. The window procedure is an unmanaged-callable static method, so no delegate has to be
/// kept alive and no marshalling stub is generated.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed unsafe class NativeWindow : IDisposable
{
    private static readonly ConcurrentDictionary<nint, NativeWindow> s_windows = new();
    private static readonly HashSet<string> s_classes = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static NativeWindow? s_creating;

    private readonly UiThread _thread;
    private readonly Func<uint, WPARAM, LPARAM, LRESULT?> _handler;

    public NativeWindow(UiThread thread, WindowSpec spec, Func<uint, WPARAM, LPARAM, LRESULT?> handler)
    {
        _thread = thread;
        _handler = handler;
        RegisterClass(spec);
        s_creating = this;
        try
        {
            fixed (char* className = spec.ClassName)
            fixed (char* title = spec.Title)
            {
                Handle = CreateWindowExW(spec.ExStyle, className, title, spec.Style, spec.X, spec.Y, spec.Width, spec.Height, HWND.NULL, HMENU.NULL, GetModuleHandleW(null), null);
            }
        }
        finally
        {
            s_creating = null;
        }

        if (Handle == HWND.NULL)
        {
            throw new Win32Exception(Marshal.GetLastSystemError(), $"CreateWindowEx failed for {spec.ClassName}");
        }

        s_windows[(nint)Handle] = this;
    }

    /// <summary>A hidden top-level window that only receives messages.</summary>
    public NativeWindow(UiThread thread, string className, string title, uint style, uint exStyle, Func<uint, WPARAM, LPARAM, LRESULT?> handler)
        : this(thread, new WindowSpec(className, title, style, exStyle, 0, 0, 0, 0), handler)
    {
    }

    public HWND Handle { get; private set; }

    public bool IsAlive => Handle != HWND.NULL;

    public void Dispose()
    {
        if (Handle != HWND.NULL)
        {
            DestroyWindow(Handle);
        }
    }

    private static void RegisterClass(WindowSpec spec)
    {
        lock (s_classes)
        {
            if (!s_classes.Add(spec.ClassName))
            {
                return;
            }

            fixed (char* name = spec.ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = name,
                    hIcon = spec.Icon,
                    hIconSm = spec.SmallIcon,
                    hCursor = LoadCursorW(HINSTANCE.NULL, IDC.IDC_ARROW),
                    hbrBackground = spec.Background is { } color ? CreateSolidBrush(color) : HBRUSH.NULL,
                };
                if (RegisterClassExW(&wc) == 0)
                {
                    s_classes.Remove(spec.ClassName);
                    throw new Win32Exception(Marshal.GetLastSystemError(), $"RegisterClassEx failed for {spec.ClassName}");
                }
            }
        }
    }

    [UnmanagedCallersOnly]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (!s_windows.TryGetValue((nint)hwnd, out var window))
        {
            window = s_creating;
            if (window is null)
            {
                return DefWindowProcW(hwnd, message, wParam, lParam);
            }

            window.Handle = hwnd;
            s_windows[(nint)hwnd] = window;
        }

        LRESULT? result = null;
        try
        {
            result = window._handler(message, wParam, lParam);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            window._thread.LogHandlerFailed(ex);
        }

        if (message == WM.WM_NCDESTROY)
        {
            s_windows.TryRemove((nint)hwnd, out _);
            window.Handle = HWND.NULL;
        }

        return result ?? DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
