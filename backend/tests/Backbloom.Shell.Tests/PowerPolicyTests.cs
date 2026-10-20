using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Engine;
using Backbloom.Settings;

namespace Backbloom.Shell.Tests;

public sealed class PowerPolicyTests
{
    private readonly FakeSystemEvents _events = new();
    private readonly ComponentStates _components = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SectionSettings _settings = new();

    private PowerPolicy Create() => new(_events, _components, _settings, _time, NullLogger<PowerPolicy>.Instance);

    [Fact]
    public void Sleep_SwitchesTheLightsOff_AndWakeUpOnAgainAfterThreeSeconds()
    {
        using var policy = Create();
        _events.Raise(SystemEvent.Suspend);
        Assert.False(_components.IsOn(Component.All));

        _events.Raise(SystemEvent.Resume);
        _time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.False(_components.IsOn(Component.All));
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.True(_components.IsOn(Component.All));
    }

    [Fact]
    public void WakeUp_KeepsLightsOffThatWereOffBeforeSleep()
    {
        using var policy = Create();
        _components.Set(Component.All, false);
        _events.Raise(SystemEvent.Suspend);
        _events.Raise(SystemEvent.Resume);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_components.IsOn(Component.All));
    }

    [Fact]
    public void WakeUp_TwiceFromOneSleep_SwitchesOnOnce()
    {
        using var policy = Create();
        var changes = 0;
        _components.Changed += (c, _) => changes += c == Component.All ? 1 : 0;
        _events.Raise(SystemEvent.Suspend);
        _events.Raise(SystemEvent.Resume);
        _events.Raise(SystemEvent.Resume);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(_components.IsOn(Component.All));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Lock_PausesCapture_UnlockResumesItAfterThreeSeconds()
    {
        using var policy = Create();
        _events.Raise(SystemEvent.SessionLocked);
        Assert.False(_components.IsOn(Component.SystemGrabber));
        Assert.True(_components.IsOn(Component.All));

        _events.Raise(SystemEvent.SessionUnlocked);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(_components.IsOn(Component.SystemGrabber));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_components.IsOn(Component.SystemGrabber));
    }

    [Fact]
    public void Lock_DoesNothing_WhenDisableOnLockedIsOff()
    {
        _settings.General = new JsonObject { ["disableOnLocked"] = false };
        using var policy = Create();
        _events.Raise(SystemEvent.SessionLocked);
        _events.Raise(SystemEvent.DisplayOff);
        Assert.True(_components.IsOn(Component.SystemGrabber));
    }

    [Fact]
    public void Lock_FollowsTheSettingWithoutARestart()
    {
        _settings.General = new JsonObject { ["disableOnLocked"] = false };
        using var policy = Create();
        _settings.General = new JsonObject { ["disableOnLocked"] = true };
        _events.Raise(SystemEvent.SessionLocked);
        Assert.False(_components.IsOn(Component.SystemGrabber));
    }

    [Fact]
    public void Unlock_WaitsForTheDisplay_WhenItIsStillOff()
    {
        using var policy = Create();
        _events.Raise(SystemEvent.DisplayOff);
        _events.Raise(SystemEvent.SessionLocked);
        _events.Raise(SystemEvent.SessionUnlocked);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(_components.IsOn(Component.SystemGrabber));

        _events.Raise(SystemEvent.DisplayOn);
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(_components.IsOn(Component.SystemGrabber));
    }

    [Fact]
    public void LockAgain_BeforeTheDelay_CancelsTheResume()
    {
        using var policy = Create();
        _events.Raise(SystemEvent.SessionLocked);
        _events.Raise(SystemEvent.SessionUnlocked);
        _time.Advance(TimeSpan.FromSeconds(1));
        _events.Raise(SystemEvent.SessionLocked);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_components.IsOn(Component.SystemGrabber));
    }

    [Fact]
    public void ASwitchByTheUser_WhileLocked_IsKept()
    {
        using var policy = Create();
        _events.Raise(SystemEvent.SessionLocked);
        _components.Set(Component.SystemGrabber, true);
        _components.Set(Component.SystemGrabber, false);
        _events.Raise(SystemEvent.SessionUnlocked);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_components.IsOn(Component.SystemGrabber));
    }

    [Fact]
    public void CaptureOffBeforeTheLock_StaysOff()
    {
        using var policy = Create();
        _components.Set(Component.SystemGrabber, false);
        _events.Raise(SystemEvent.SessionLocked);
        _events.Raise(SystemEvent.SessionUnlocked);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_components.IsOn(Component.SystemGrabber));
    }

    [Fact]
    public void Dispose_StopsReacting()
    {
        var policy = Create();
        _events.Raise(SystemEvent.Suspend);
        _events.Raise(SystemEvent.Resume);
        policy.Dispose();
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(_components.IsOn(Component.All));
        _components.Set(Component.All, true);
        _events.Raise(SystemEvent.Suspend);
        Assert.True(_components.IsOn(Component.All));
    }

    /// <summary>Only <c>general</c> is read by the policy.</summary>
    private sealed class SectionSettings : ISettingsStore
    {
        public event Action<SettingsChange>? Changed
        {
            add { }
            remove { }
        }

        public JsonObject? General { get; set; }

        public IReadOnlyList<ControllerSettings> Controllers => [];

        public JsonNode? GetSection(string section) => section == "general" ? General : null;

        public JsonObject Snapshot() => [];

        public SettingsUpdateResult Update(JsonObject sections) => throw new NotSupportedException();

        public SettingsContent Export() => throw new NotSupportedException();

        public SettingsUpdateResult Replace(SettingsContent content) => throw new NotSupportedException();
    }
}
