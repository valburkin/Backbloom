using System.Drawing;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Backbloom.Shell;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Platform.Windows.Desktop;

/// <summary>
/// The app window: a plain Win32 window hosting the web UI in WebView2 (Microsoft.Web.WebView2.Core, no WinForms or
/// WPF). It remembers its placement (<see cref="WindowPlacementStore"/>); closing it destroys the window and its
/// browser processes, the app keeps running in the tray. Links to other sites open in the default browser. Without
/// the WebView2 runtime the UI opens in the default browser.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed partial class WebViewWindow : IAppWindow
{
    private const string ClassName = "BackbloomWindow";
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 860;
    private const int MinimumWidth = 480;
    private const int MinimumHeight = 360;

    /// <summary>The UI's background (#090b10) as COLORREF, painted until the page draws.</summary>
    private const uint Background = 0x00100B09;

    private readonly UiThread _ui;
    private readonly Uri _url;
    private readonly WindowPlacementStore _placement;
    private readonly string _userDataFolder;
    private readonly IBrowserLauncher _browser;
    private readonly ILogger _logger;
    private NativeWindow? _window;
    private CoreWebView2Controller? _controller;
    private HICON _icon;
    private HICON _smallIcon;

    public WebViewWindow(UiThread ui, Uri url, WindowPlacementStore placement, string userDataFolder, IBrowserLauncher browser, ILogger<WebViewWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentException.ThrowIfNullOrEmpty(userDataFolder);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(logger);
        _ui = ui;
        _url = url;
        _placement = placement;
        _userDataFolder = userDataFolder;
        _browser = browser;
        _logger = logger;
    }

    /// <summary>The installed WebView2 runtime version; null when there is none.</summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }

    public void Show() => _ui.Post(ShowOnUiThread);

    public void Dispose() => _ui.Invoke(
        () =>
        {
            Close(byUser: false);
            DestroyIcons();
        },
        TimeSpan.FromSeconds(5));

    private unsafe void ShowOnUiThread()
    {
        if (_window is { IsAlive: true } existing)
        {
            if (IsIconic(existing.Handle) != FALSE)
            {
                ShowWindow(existing.Handle, SW.SW_RESTORE);
            }

            SetForegroundWindow(existing.Handle);
            return;
        }

        if (RuntimeVersion() is not { } version)
        {
            LogNoRuntime(_url);
            _browser.Open(_url);
            return;
        }

        if (_icon == HICON.NULL)
        {
            _icon = AppIcon.Create(GetSystemMetrics(SM.SM_CXICON));
            _smallIcon = AppIcon.Create(GetSystemMetrics(SM.SM_CXSMICON));
        }

        var scale = GetDpiForSystem() / 96.0;
        var spec = new WindowSpec(
            ClassName,
            "Backbloom",
            WS.WS_OVERLAPPEDWINDOW,
            0,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            (int)(DefaultWidth * scale),
            (int)(DefaultHeight * scale))
        {
            Icon = _icon,
            SmallIcon = _smallIcon,
            Background = Background,
        };
        _window = new NativeWindow(_ui, spec, OnMessage);
        var hwnd = _window.Handle;
        var dark = TRUE;
        DwmSetWindowAttribute(hwnd, (uint)DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, (uint)sizeof(BOOL));
        Restore(hwnd);
        LogOpened(version);
        _ = InitializeAsync(hwnd);
    }

    private unsafe void Restore(HWND hwnd)
    {
        var show = SW.SW_SHOWNORMAL;
        if (_placement.Load() is { } saved)
        {
            var rect = new RECT(saved.X, saved.Y, saved.X + saved.Width, saved.Y + saved.Height);
            if (MonitorFromRect(&rect, MONITOR.MONITOR_DEFAULTTONULL) != HMONITOR.NULL)
            {
                var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT), rcNormalPosition = rect, showCmd = SW.SW_HIDE };
                SetWindowPlacement(hwnd, &placement);
                show = saved.Maximized ? SW.SW_SHOWMAXIMIZED : SW.SW_SHOWNORMAL;
            }
        }

        ShowWindow(hwnd, show);
        SetForegroundWindow(hwnd);
    }

    private async Task InitializeAsync(HWND hwnd)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            var controller = await environment.CreateCoreWebView2ControllerAsync((nint)hwnd);
            if (_window?.Handle != hwnd)
            {
                // closed while the browser was starting
                controller.Close();
                return;
            }

            _controller = controller;
            controller.DefaultBackgroundColor = Color.FromArgb(0x09, 0x0B, 0x10);
            Resize();
            var core = controller.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!IsAppPage(e.Uri))
                {
                    e.Cancel = true;
                    OpenExternal(e.Uri);
                }
            };
            core.Navigate(_url.AbsoluteUri);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogWebViewFailed(ex);
            Close(byUser: false);
            _browser.Open(_url);
        }
    }

    private bool IsAppPage(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var target) && Uri.Compare(target, _url, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var target) && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps))
        {
            _browser.Open(target);
        }
    }

    private unsafe LRESULT? OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WM.WM_SIZE:
                Resize();
                return null;
            case WM.WM_MOVE:
                _controller?.NotifyParentWindowPositionChanged();
                return null;
            case WM.WM_GETMINMAXINFO:
                var info = (MINMAXINFO*)(nint)lParam;
                var scale = _window is { IsAlive: true } w ? GetDpiForWindow(w.Handle) / 96.0 : 1.0;
                info->ptMinTrackSize = new POINT((int)(MinimumWidth * scale), (int)(MinimumHeight * scale));
                return 0;
            case WM.WM_DPICHANGED:
                var suggested = (RECT*)(nint)lParam;
                SetWindowPos(_window!.Handle, HWND.NULL, suggested->left, suggested->top, suggested->right - suggested->left, suggested->bottom - suggested->top, SWP.SWP_NOZORDER | SWP.SWP_NOACTIVATE);
                return 0;
            case WM.WM_CLOSE:
                Close(byUser: true);
                return 0;
            default:
                return null;
        }
    }

    private unsafe void Resize()
    {
        if (_controller is null || _window is not { IsAlive: true } window)
        {
            return;
        }

        RECT client;
        GetClientRect(window.Handle, &client);
        _controller.Bounds = new Rectangle(0, 0, client.right - client.left, client.bottom - client.top);
    }

    private void Close(bool byUser)
    {
        if (_window is not { IsAlive: true } window)
        {
            return;
        }

        SavePlacement(window.Handle);
        _controller?.Close();
        _controller = null;
        window.Dispose();
        _window = null;
        if (byUser)
        {
            LogClosed();
        }
    }

    private unsafe void SavePlacement(HWND hwnd)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (GetWindowPlacement(hwnd, &placement) == FALSE)
        {
            return;
        }

        var r = placement.rcNormalPosition;
        var saved = new WindowPlacement(r.left, r.top, r.right - r.left, r.bottom - r.top, placement.showCmd == SW.SW_SHOWMAXIMIZED);
        if (saved.IsUsable)
        {
            _placement.Save(saved);
        }
    }

    private void DestroyIcons()
    {
        if (_icon != HICON.NULL)
        {
            DestroyIcon(_icon);
            DestroyIcon(_smallIcon);
            _icon = _smallIcon = HICON.NULL;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Window opened (WebView2 {Version})")]
    private partial void LogOpened(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Window closed; Backbloom keeps running in the tray")]
    private partial void LogClosed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The WebView2 runtime isn't installed; opening {Url} in the default browser")]
    private partial void LogNoRuntime(Uri url);

    [LoggerMessage(Level = LogLevel.Error, Message = "WebView2 couldn't start; opening the UI in the default browser")]
    private partial void LogWebViewFailed(Exception exception);
}
