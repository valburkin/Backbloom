using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Overglow.Shell;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Overglow.Platform.Windows.Desktop;

/// <summary>
/// Power and session notifications (port of SuspendHandlerWindows): sleep / wake-up (<c>WM_POWERBROADCAST</c>
/// after <c>RegisterSuspendResumeNotification</c>), display on/off (<c>GUID_SESSION_DISPLAY_STATUS</c>), lock /
/// unlock (<c>WTSRegisterSessionNotification</c>; a lock in a remote desktop session is ignored, as in C++) and the end
/// of the session (<c>WM_ENDSESSION</c>, which C++ didn't handle: Windows ended the process with the LEDs still on).
/// Messages arrive on the hidden window of the <see cref="UiThread"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed unsafe partial class WindowsSystemEvents : ISystemEvents, IDisposable
{
    internal const uint PowerBroadcast = WM.WM_POWERBROADCAST;
    internal const uint SessionChange = 0x02B1;
    internal const uint QueryEndSession = WM.WM_QUERYENDSESSION;
    internal const uint EndSession = WM.WM_ENDSESSION;
    internal const nuint PbtSuspend = 0x0004;
    internal const nuint PbtResumeSuspend = 0x0007;
    internal const nuint PbtResumeAutomatic = 0x0012;
    internal const nuint PbtPowerSettingChange = 0x8013;
    internal const nuint WtsSessionLock = 0x7;
    internal const nuint WtsSessionUnlock = 0x8;
    private const uint NotifyForThisSession = 0;
    private const uint DeviceNotifyWindowHandle = 0;

    /// <summary>GUID_SESSION_DISPLAY_STATUS: data 0 = off, 1 = on, 2 = dimmed.</summary>
    internal static readonly Guid DisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    private readonly UiThread _ui;
    private readonly ILogger _logger;
    private HPOWERNOTIFY _suspend;
    private HPOWERNOTIFY _display;
    private bool _session;

    public WindowsSystemEvents(UiThread ui, ILogger<WindowsSystemEvents> logger)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(logger);
        _ui = ui;
        _logger = logger;
        _ui.Invoke(Register, TimeSpan.FromSeconds(5));
    }

    public event Action<SystemEvent>? Raised;

    public void Dispose() => _ui.Invoke(Unregister, TimeSpan.FromSeconds(5));

    /// <summary>
    /// The event a window message stands for (pure, for tests). <paramref name="setting"/> is the
    /// <c>POWERBROADCAST_SETTING</c> of a <c>PBT_POWERSETTINGCHANGE</c>.
    /// </summary>
    internal static SystemEvent? Translate(uint message, nuint wParam, (Guid Id, uint Data)? setting, bool remoteSession) => message switch
    {
        PowerBroadcast when wParam == PbtSuspend => SystemEvent.Suspend,
        PowerBroadcast when wParam is PbtResumeSuspend or PbtResumeAutomatic => SystemEvent.Resume,
        PowerBroadcast when wParam == PbtPowerSettingChange && setting is { } s && s.Id == DisplayStatus => s.Data switch
        {
            0 => SystemEvent.DisplayOff,
            1 => SystemEvent.DisplayOn,
            _ => null,
        },
        SessionChange when wParam == WtsSessionLock && !remoteSession => SystemEvent.SessionLocked,
        SessionChange when wParam == WtsSessionUnlock => SystemEvent.SessionUnlocked,
        EndSession when wParam != 0 => SystemEvent.EndSession,
        _ => null,
    };

    /// <summary>Raises <paramref name="e"/> as if Windows had sent it (tests).</summary>
    internal void Raise(SystemEvent e) => Raised?.Invoke(e);

    private void Register()
    {
        var hwnd = _ui.MessageWindow;
        _suspend = RegisterSuspendResumeNotification((HANDLE)hwnd, DeviceNotifyWindowHandle);
        var display = DisplayStatus;
        _display = RegisterPowerSettingNotification((HANDLE)hwnd, &display, DeviceNotifyWindowHandle);
        _session = WTSRegisterSessionNotification((nint)hwnd, NotifyForThisSession) != 0;
        if (_suspend == HPOWERNOTIFY.NULL || _display == HPOWERNOTIFY.NULL || !_session)
        {
            LogRegistrationFailed(_suspend != HPOWERNOTIFY.NULL, _display != HPOWERNOTIFY.NULL, _session);
        }

        _ui.AddMessageHandler(OnMessage);
    }

    private void Unregister()
    {
        if (_suspend != HPOWERNOTIFY.NULL)
        {
            UnregisterSuspendResumeNotification(_suspend);
            _suspend = HPOWERNOTIFY.NULL;
        }

        if (_display != HPOWERNOTIFY.NULL)
        {
            UnregisterPowerSettingNotification(_display);
            _display = HPOWERNOTIFY.NULL;
        }

        if (_session)
        {
            _ = WTSUnRegisterSessionNotification((nint)_ui.MessageWindow);
            _session = false;
        }
    }

    private LRESULT? OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == QueryEndSession)
        {
            return 1;
        }

        if (message is not (PowerBroadcast or SessionChange or EndSession))
        {
            return null;
        }

        (Guid, uint)? setting = null;
        if (message == PowerBroadcast && wParam == PbtPowerSettingChange && lParam != 0)
        {
            var s = (POWERBROADCAST_SETTING*)(nint)lParam;
            setting = (s->PowerSetting, s->DataLength > 0 ? s->Data[0] : uint.MaxValue);
        }

        if (Translate(message, wParam, setting, GetSystemMetrics(SM.SM_REMOTESESSION) != 0) is { } e)
        {
            LogEvent(e);
            Raised?.Invoke(e);
            return message == PowerBroadcast ? 1 : 0;
        }

        if (message == SessionChange && wParam == WtsSessionLock)
        {
            LogRemoteLockIgnored();
        }

        return null;
    }

    [LibraryImport("wtsapi32.dll")]
    private static partial int WTSRegisterSessionNotification(nint hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll")]
    private static partial int WTSUnRegisterSessionNotification(nint hWnd);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OS event: {Event}")]
    private partial void LogEvent(SystemEvent @event);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Remote desktop session: the lock is ignored")]
    private partial void LogRemoteLockIgnored();

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't register for power and session events (sleep: {Sleep}, display: {Display}, lock: {Lock})")]
    private partial void LogRegistrationFailed(bool sleep, bool display, bool @lock);
}
