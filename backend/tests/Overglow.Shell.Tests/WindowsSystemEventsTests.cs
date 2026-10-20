using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Overglow.Core.Priorities;
using Overglow.Engine;
using Overglow.Platform.Windows.Desktop;
using static Overglow.Platform.Windows.Desktop.WindowsSystemEvents;

namespace Overglow.Shell.Tests;

/// <summary>
/// The Windows notifications as the window procedure receives them. The session is never really locked: the messages
/// Windows would send are sent to the app's hidden window instead.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed partial class WindowsSystemEventsTests
{
    [Theory]
    [InlineData(PowerBroadcast, (ulong)PbtSuspend, SystemEvent.Suspend)]
    [InlineData(PowerBroadcast, (ulong)PbtResumeSuspend, SystemEvent.Resume)]
    [InlineData(PowerBroadcast, (ulong)PbtResumeAutomatic, SystemEvent.Resume)]
    [InlineData(SessionChange, (ulong)WtsSessionLock, SystemEvent.SessionLocked)]
    [InlineData(SessionChange, (ulong)WtsSessionUnlock, SystemEvent.SessionUnlocked)]
    [InlineData(EndSession, 1UL, SystemEvent.EndSession)]
    public void Translate_MapsTheMessages(uint message, ulong wParam, SystemEvent expected) =>
        Assert.Equal(expected, Translate(message, (nuint)wParam, null, remoteSession: false));

    [Fact]
    public void Translate_IgnoresALockInARemoteSession_AsCppDid()
    {
        Assert.Null(Translate(SessionChange, WtsSessionLock, null, remoteSession: true));
        Assert.Equal(SystemEvent.SessionUnlocked, Translate(SessionChange, WtsSessionUnlock, null, remoteSession: true));
    }

    [Fact]
    public void Translate_ReadsTheDisplayState()
    {
        Assert.Equal(SystemEvent.DisplayOff, Translate(PowerBroadcast, PbtPowerSettingChange, (DisplayStatus, 0), false));
        Assert.Equal(SystemEvent.DisplayOn, Translate(PowerBroadcast, PbtPowerSettingChange, (DisplayStatus, 1), false));
        Assert.Null(Translate(PowerBroadcast, PbtPowerSettingChange, (DisplayStatus, 2), false));
        Assert.Null(Translate(PowerBroadcast, PbtPowerSettingChange, (Guid.NewGuid(), 0), false));
    }

    [Fact]
    public void Translate_IgnoresACanceledSessionEnd() =>
        Assert.Null(Translate(EndSession, 0, null, remoteSession: false));

    [Fact]
    public async Task LockMessage_ToTheHiddenWindow_PausesCapture_UnlockResumesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Windows only");
        if (GetSystemMetrics(0x1000) != 0)
        {
            Assert.Skip("Remote desktop session: locks are ignored there.");
        }

        await using var host = await TestHost.StartAsync("""{ "general": { "disableOnLocked": true } }""");
        var window = host.App.Services.GetRequiredService<UiThread>().MessageWindow;
        var components = host.Engine.Components;

        // the screen is a source once it delivers its first frame
        Wait.ForScreen(host.Engine);

        SendMessageW((nint)window, SessionChange, WtsSessionLock, 1);
        Assert.False(components.IsOn(Component.SystemGrabber));
        Assert.False(host.Engine.Sources.Visible.HasSource);

        SendMessageW((nint)window, SessionChange, WtsSessionUnlock, 1);
        Assert.False(components.IsOn(Component.SystemGrabber));
        Wait.ForComponent(host.Engine, Component.SystemGrabber, true, 6000);
        Wait.ForScreen(host.Engine);
    }

    [Fact]
    public async Task Lock_KeepsAnEffectShowing_AsCppDid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Windows only");
        if (GetSystemMetrics(0x1000) != 0)
        {
            Assert.Skip("Remote desktop session: locks are ignored there.");
        }

        await using var host = await TestHost.StartAsync("""{ "general": { "disableOnLocked": true } }""");
        var window = host.App.Services.GetRequiredService<UiThread>().MessageWindow;
        Assert.True(host.Engine.RunEffect("Candle", 1, 0, "test"));

        SendMessageW((nint)window, SessionChange, WtsSessionLock, 1);
        Assert.False(host.Engine.Components.IsOn(Component.SystemGrabber));
        Assert.Equal(SourceComponent.Effect, host.Engine.Sources.Visible.Component);
        Assert.True(host.Engine.Components.OutputActive);
        SendMessageW((nint)window, SessionChange, WtsSessionUnlock, 1);
    }

    [Fact]
    public async Task SuspendMessage_SwitchesTheLightsOff()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Windows only");
        await using var host = await TestHost.StartAsync();
        var window = host.App.Services.GetRequiredService<UiThread>().MessageWindow;
        SendMessageW((nint)window, PowerBroadcast, PbtSuspend, 0);
        Assert.False(host.Engine.Components.IsOn(Component.All));
        SendMessageW((nint)window, PowerBroadcast, PbtResumeAutomatic, 0);
        Wait.ForComponent(host.Engine, Component.All, true, 6000);
    }

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
