# Desktop shell

What makes Backbloom a desktop app: single instance, tray, window, autostart, power and session handling, logging and
shutdown.

This is a port of HyperHDR's `sources/app` (main, AppDaemon, SystrayHandler), `systray` and `suspend-handler`,
reorganized: the platform-neutral parts live in `Backbloom.Shell`, the Windows parts in
`Backbloom.Platform.Windows/Desktop`, and `App` wires them (`DesktopRegistration`).

## Contents

- [Features](#features)
- [Design choices](#design-choices)

## Features

| Feature | Behavior |
|---|---|
| Single instance | One app per data directory (HyperHDR: one executable per machine, a second start failed). The first instance holds `backbloom.lock` in the data directory exclusively (released by the OS even after a crash) and listens on a named pipe derived from the directory (current user only). A second start with the same directory asks it to show its window (headless: the second process opens the UI in the browser) and exits; `--quit` asks it to quit and waits. Instances with different data directories run side by side. |
| Tray | `Shell_NotifyIcon` (version 4) with the Backbloom icon (`branding/backbloom.ico`, embedded; the best size is picked from the .ico). The icon comes back after an Explorer restart (`TaskbarCreated`). See [Tray menu](#tray-menu) below. |
| Window | A plain Win32 window hosting the UI in WebView2 (`Microsoft.Web.WebView2.Core` only, BSD-3; no WinForms/WPF, whose assemblies are kept out of the output by `Directory.Build.targets`). Dark title bar, the UI's background until the page draws, placement (normal bounds + maximized) in `window.json` (atomic write; restored only when it is on a monitor). Closing it destroys the window and its browser processes (no memory held while hidden); the app stays in the tray. Links to other sites open in the default browser. Without the WebView2 runtime (`GetAvailableBrowserVersionString`) or when it fails to start, the UI opens in the default browser. Profile in `<data>/webview`. |
| Autostart | `RegistryAutostart`: value `Backbloom` in `HKCU\...\CurrentVersion\Run` (as HyperHDR did), command `"<exe>" --no-window` plus `--data-dir` when it isn't the default; enabled only when the entry starts this executable (also HyperHDR's bare-path entries). Off by default: only the user turns it on (tray menu, the first-run setup's checkbox, `setup autostart`). The key is a constructor parameter; the tests use a scratch key and never write the real Run key. |
| Power and session | `WindowsSystemEvents` (hidden top-level window: `RegisterSuspendResumeNotification`, `RegisterPowerSettingNotification(GUID_SESSION_DISPLAY_STATUS)`, `WTSRegisterSessionNotification`, `WM_ENDSESSION`) raises `ISystemEvents`. `PowerPolicy` (port of `InstanceManager::hibernate`) reacts: sleep -> `ALL` off, wake-up -> on 3 s later; locked or display off with `general.disableOnLocked` -> `SYSTEMGRABBER` off (the screen source leaves, so the LEDs go dark unless a color or effect shows: an effect chosen by the user keeps running while locked, as in HyperHDR; without this setting the screen still leaves 800 ms after the lock screen stops its frames); unlocked and display on -> capture on 3 s later, and the screen shows again with its first frame. A lock in a remote desktop session is ignored (as in HyperHDR). |
| Startup | `general.disableLedsStartup` starts with `LEDDEVICE` off (engine, unchanged); `--no-window` keeps the window closed, except until the first-run setup was completed (the window always opens then, so the setup is seen). |
| Logging | Console (when there is one), `logs/backbloom-yyyyMMdd.log` in the data directory and the UI's log view, at the level of the `logger` section (silent = errors only, warn, verbose = info, debug), applied at once when it changes. One plain-text line per message (time, level, category, rendered message template; exceptions indented below), written by a background thread; a new file per day and per 10 MB, the newest 10 kept. A failed start or a crash (unhandled exception) is appended there as a `FATAL` entry by `CrashReport` (directly, so it is recorded before logging exists and when the process ends; with the [recent performance](performance-recording.md#crash-reports) when there is any), printed to the console if there is one, and shown in a message box (`ErrorDialog`) unless `--service`. |
| Shutdown | Quit, `--quit` and Ctrl+C stop the host: WebSocket clients are closed at once, the engine switches every controller off (black frame, then closed), then the tray icon goes. Where Windows ends the process as soon as a notification returns (`WM_ENDSESSION` at logoff/shutdown; console close and shutdown signals), the handler stops the host and waits up to 4.5 s for the LEDs (`GracefulShutdown`). HyperHDR didn't handle the session end. |
| Settings | Crash-safe: every change is one SQLite transaction (rollback journal, `synchronous=FULL` by default); `window.json` is written to a temporary file and moved over. |
| Packaging | `backbloom.exe` is a `WinExe` with the Backbloom icon; the built UI is copied next to it (`ui/`, build and publish) when `ui/dist` exists. `packaging/build.ps1` publishes a self-contained ReadyToRun folder and packs the Velopack installer with automatic updates; see [packaging/README.md](../../packaging/README.md). |

### Tray menu

The menu is rebuilt on each open (`TrayMenu`):

- **Open Backbloom** (default, also a click on the icon);
- **Lights** (`ALL`);
- **Screen sync** (removes colors and effects, capture on; checked while the screen shows or waits for its first frame);
- **Music** (the music effects), **Effect** (the six effects), **Color** (HyperHDR's colors): music, effects and colors
  run at priority 1 until something else is chosen, as in HyperHDR; the running one is checked;
- **Pause for 30 minutes** (then *Resume*; a switch elsewhere ends the pause);
- **Start with Windows**;
- **Quit**.

HyperHDR's "Instances" submenu is gone (Backbloom runs one pipeline).

## Design choices

- **WebView2 directly, not Photino.NET.** The tray needs native code on every OS anyway (`Shell_NotifyIcon`,
  `NSStatusItem`, StatusNotifierItem), and the window only shows one local page, so a cross-platform window library
  saves little. Hosting WebView2 in an own Win32 window gives what the shell needs and Photino doesn't: hiding vs.
  destroying on close, placement with `Get/SetWindowPlacement`, the end-session message, one message loop for tray,
  notifications and window, no second native library. macOS/Linux would implement `ITrayIcon` / `IAppWindow` /
  `IAutostart` / `ISystemEvents` in their own projects (WKWebView/WebKitGTK, or Photino behind `IAppWindow`).
- **Interop.** TerraFX.Interop.Windows (already used by capture) for user32/shell32/dwmapi, an
  `[UnmanagedCallersOnly]` window procedure, and `LibraryImport` for the two wtsapi32 calls TerraFX lacks. The WebView2
  wrapper uses built-in COM interop, so `Platform.Windows` is not marked AOT-compatible.
- **One UI thread** (STA) owns every window: WebView2 and the menu loop need a message pump. It has a
  `SynchronizationContext`, so `await` in the window code returns to it.
