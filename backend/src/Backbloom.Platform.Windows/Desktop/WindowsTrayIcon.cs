using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Backbloom.Shell;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Backbloom.Platform.Windows.Desktop;

/// <summary>
/// The notification area icon (port of SystrayWindows: <c>Shell_NotifyIcon</c> with the version 4 callbacks). A click
/// runs the default action, the context menu is a popup menu built from <see cref="TrayMenuItem"/>s each time it
/// opens; the icon is added again when Explorer restarts (<c>TaskbarCreated</c>).
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed unsafe partial class WindowsTrayIcon : ITrayIcon
{
    private const uint CallbackMessage = WM.WM_APP + 2;
    private const uint IconId = 1;
    private const uint SelectNotification = WM.WM_USER;
    private const uint KeySelectNotification = WM.WM_USER + 1;

    private readonly UiThread _ui;
    private readonly ILogger _logger;
    private readonly uint _taskbarCreated;
    private string _tooltip = "Backbloom";
    private Func<IReadOnlyList<TrayMenuItem>> _menu = () => [];
    private Action _activated = () => { };
    private HICON _icon;
    private bool _shown;
    private bool _menuOpen;

    public WindowsTrayIcon(UiThread ui, ILogger<WindowsTrayIcon> logger)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(logger);
        _ui = ui;
        _logger = logger;
        fixed (char* name = "TaskbarCreated")
        {
            _taskbarCreated = RegisterWindowMessageW(name);
        }

        _ui.Invoke(() => _ui.AddMessageHandler(OnMessage), TimeSpan.FromSeconds(5));
    }

    public void Show(string tooltip, Func<IReadOnlyList<TrayMenuItem>> menu, Action activated)
    {
        ArgumentNullException.ThrowIfNull(tooltip);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(activated);
        _ui.Invoke(
            () =>
            {
                _tooltip = tooltip;
                _menu = menu;
                _activated = activated;
                if (_icon == HICON.NULL)
                {
                    _icon = AppIcon.Create(GetSystemMetrics(SM.SM_CXSMICON));
                }

                _shown = Add();
            },
            TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _ui.Invoke(
        () =>
        {
            if (_shown)
            {
                var data = Data(0);
                Shell_NotifyIconW(NIM_DELETE, &data);
                _shown = false;
            }

            if (_icon != HICON.NULL)
            {
                DestroyIcon(_icon);
                _icon = HICON.NULL;
            }
        },
        TimeSpan.FromSeconds(5));

    private NOTIFYICONDATAW Data(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _ui.MessageWindow,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = _tooltip.AsSpan(0, Math.Min(_tooltip.Length, 127));
        tip.CopyTo(new Span<char>(&data.szTip, 128));
        return data;
    }

    private bool Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (Shell_NotifyIconW(NIM_ADD, &data) == FALSE)
        {
            // still there from before an Explorer restart: update it
            if (Shell_NotifyIconW(NIM_MODIFY, &data) == FALSE)
            {
                LogAddFailed();
                return false;
            }
        }

        data.Anonymous.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
        return true;
    }

    private LRESULT? OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            if (_icon != HICON.NULL)
            {
                _shown = Add();
            }

            return null;
        }

        if (message != CallbackMessage)
        {
            return null;
        }

        switch ((uint)(lParam & 0xFFFF))
        {
            case SelectNotification:
            case KeySelectNotification:
                _activated();
                break;
            case WM.WM_CONTEXTMENU:
                ShowMenu();
                break;
        }

        return 0;
    }

    private void ShowMenu()
    {
        if (_menuOpen)
        {
            return;
        }

        var actions = new List<Action?>();
        var menu = Build(_menu(), actions);
        try
        {
            POINT cursor;
            GetCursorPos(&cursor);
            var hwnd = _ui.MessageWindow;

            // the menu closes on a click elsewhere only while our window is in the foreground (KB135788)
            SetForegroundWindow(hwnd);
            _menuOpen = true;
            var chosen = TrackPopupMenuEx(menu, TPM.TPM_RETURNCMD | TPM.TPM_RIGHTBUTTON | TPM.TPM_NONOTIFY, cursor.x, cursor.y, hwnd, null);
            _menuOpen = false;
            PostMessageW(hwnd, WM.WM_NULL, default, default);
            if (chosen > 0 && chosen <= actions.Count)
            {
                actions[chosen - 1]?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static HMENU Build(IReadOnlyList<TrayMenuItem> items, List<Action?> actions)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                AppendMenuW(menu, MF.MF_SEPARATOR, 0, null);
                continue;
            }

            var flags = (uint)MF.MF_STRING | (item.Checked ? MF.MF_CHECKED : 0u) | (item.Enabled ? 0u : MF.MF_GRAYED);
            nuint id;
            if (item.Children is { Count: > 0 } children)
            {
                flags |= MF.MF_POPUP;
                id = (nuint)(nint)Build(children, actions);
            }
            else
            {
                actions.Add(item.Invoke);
                id = (nuint)actions.Count;
            }

            fixed (char* text = item.Text.Replace("&", "&&", StringComparison.Ordinal))
            {
                AppendMenuW(menu, flags, id, text);
            }

            if (item.IsDefault && item.Children is null)
            {
                SetMenuDefaultItem(menu, (uint)id, FALSE);
            }
        }

        return menu;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't add the tray icon")]
    private partial void LogAddFailed();
}
