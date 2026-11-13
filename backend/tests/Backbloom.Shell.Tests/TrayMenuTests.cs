using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Backbloom.Core.Color;
using Backbloom.Core.Priorities;
using Backbloom.Engine;

namespace Backbloom.Shell.Tests;

public sealed class TrayMenuTests : IDisposable
{
    private readonly FakeWindow _window = new();
    private readonly FakeAutostart _autostart = new();
    private readonly FakeTimeProvider _time = new();
    private int _quits;

    public void Dispose() => _window.Dispose();

    private TrayMenu Create(TestHost host) =>
        new(host.Engine, _window, _autostart, () => _quits++, _time, NullLogger<TrayMenu>.Instance);

    private static TrayMenuItem Item(IReadOnlyList<TrayMenuItem> menu, string text) => menu.Single(i => i.Text == text);

    private static TrayMenuItem PauseItem(TrayMenu tray) =>
        tray.Build().Single(i => i.Text.StartsWith("Pause", StringComparison.Ordinal) || i.Text.StartsWith("Resume", StringComparison.Ordinal));

    [Fact]
    public async Task Menu_HasTheEntries_InOrder()
    {
        await using var host = await TestHost.StartAsync();
        Wait.ForScreen(host.Engine);
        using var tray = Create(host);
        var menu = tray.Build();
        Assert.Equal(
            ["Open Backbloom", "-", "Lights", "Screen sync", "Music", "Effect", "Color", "Pause for 30 minutes", "-", "Start with the OS", "-", "Quit"],
            menu.Select(i => i.Text));
        Assert.True(Item(menu, "Open Backbloom").IsDefault);
        Assert.True(Item(menu, "Lights").Checked);
        Assert.True(Item(menu, "Screen sync").Checked);
        Assert.False(Item(menu, "Music").Checked);
        Assert.False(Item(menu, "Effect").Checked);
        Assert.False(Item(menu, "Color").Checked);
        Assert.Equal(
            ["Fullscreen pulse", "Quatro", "Stereo", "Pulse waves", "Spectrum", "VU meter", "Beat ripple", "Waterfall", "Bass glow"],
            Item(menu, "Music").Children!.Where(c => c.Enabled && !c.IsSeparator).Select(c => c.Text));
        Assert.Equal(["Rainbow swirl", "Rainbow swirl fast", "Full color mood blobs", "Warm mood blobs", "Breath", "Candle"], Item(menu, "Effect").Children!.Select(c => c.Text));
        Assert.Equal(TrayMenu.Colors.Select(c => c.Name), Item(menu, "Color").Children!.Select(c => c.Text));
        Assert.False(Item(menu, "Start with the OS").Checked);
    }

    [Fact]
    public async Task Effects_AndMusic_RunUntilSomethingElseIsChosen()
    {
        await using var host = await TestHost.StartAsync();
        using var tray = Create(host);
        Item(tray.Build(), "Effect").Children!.Single(c => c.Text == "Breath").Invoke!();
        var visible = host.Engine.Sources.Visible;
        Assert.Equal(SourceComponent.Effect, visible.Component);
        Assert.Equal(TrayMenu.ColorPriority, visible.Priority);
        Assert.Equal("Breath", (visible.Renderer as EffectSource)?.Definition.Name);
        var menu = tray.Build();
        Assert.True(Item(menu, "Effect").Checked);
        Assert.True(Item(menu, "Effect").Children!.Single(c => c.Text == "Breath").Checked);
        Assert.False(Item(menu, "Screen sync").Checked);

        var music = Item(tray.Build(), "Music").Children![0];
        music.Invoke!();
        Assert.True((host.Engine.Sources.Visible.Renderer as EffectSource)?.Definition.IsMusic);
        Assert.True(Item(tray.Build(), "Music").Children![0].Checked);
        Assert.Equal([TrayMenu.ColorPriority], host.Engine.Effects.Active.Select(e => e.Priority));

        // without sound capture the music effect ends and the music entries can't be chosen
        Assert.True(host.Settings.Update(new JsonObject { ["soundEffect"] = new JsonObject { ["enable"] = false, ["device"] = "default", ["enable_smoothing"] = true } }).Success);
        Assert.Empty(host.Engine.Effects.Active);
        var off = Item(tray.Build(), "Music").Children!;
        Assert.Equal("Sound input is off", off[0].Text);
        Assert.All(off.Where(c => !c.IsSeparator), c => Assert.False(c.Enabled));
        Assert.True(host.Settings.Update(new JsonObject { ["soundEffect"] = new JsonObject { ["enable"] = true, ["device"] = "default", ["enable_smoothing"] = true } }).Success);
        Assert.All(Item(tray.Build(), "Music").Children!, c => Assert.True(c.Enabled));

        // a color at the same priority replaces the effect
        Item(tray.Build(), "Color").Children!.Single(c => c.Text == "Red").Invoke!();
        Assert.Equal(SourceComponent.Color, host.Engine.Sources.Visible.Component);
    }

    [Fact]
    public async Task Entries_OpenTheWindow_SwitchTheLights_AndQuit()
    {
        await using var host = await TestHost.StartAsync();
        using var tray = Create(host);
        Item(tray.Build(), "Open Backbloom").Invoke!();
        Assert.Equal(1, _window.Shown);

        Item(tray.Build(), "Lights").Invoke!();
        Assert.False(host.Engine.Components.IsOn(Component.All));
        Assert.False(Item(tray.Build(), "Lights").Checked);
        Item(tray.Build(), "Lights").Invoke!();
        Assert.True(host.Engine.Components.IsOn(Component.All));

        Item(tray.Build(), "Quit").Invoke!();
        Assert.Equal(1, _quits);
    }

    [Fact]
    public async Task Color_ShowsAStaticColor_ScreenSyncClearsIt()
    {
        await using var host = await TestHost.StartAsync();
        using var tray = Create(host);
        Item(tray.Build(), "Color").Children!.Single(c => c.Text == "Blue").Invoke!();
        var visible = host.Engine.Sources.Visible;
        Assert.Equal(SourceComponent.Color, visible.Component);
        Assert.Equal(new Rgb8(0, 0, 255), visible.Color);
        Assert.True(Item(tray.Build(), "Color").Children!.Single(c => c.Text == "Blue").Checked);
        Assert.False(Item(tray.Build(), "Screen sync").Checked);

        host.Engine.Components.Set(Component.SystemGrabber, false);
        Item(tray.Build(), "Screen sync").Invoke!();
        Wait.ForScreen(host.Engine);
        Assert.True(host.Engine.Components.IsOn(Component.SystemGrabber));
    }

    [Fact]
    public async Task Pause_SwitchesOffForThirtyMinutes()
    {
        await using var host = await TestHost.StartAsync();
        using var tray = Create(host);
        Item(tray.Build(), "Pause for 30 minutes").Invoke!();
        Assert.False(host.Engine.Components.IsOn(Component.All));
        Assert.StartsWith("Resume (paused until ", PauseItem(tray).Text, StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.False(host.Engine.Components.IsOn(Component.All));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(host.Engine.Components.IsOn(Component.All));
        Assert.Null(tray.PausedUntil);
    }

    [Fact]
    public async Task Pause_EndsWhenTheLightsAreSwitchedOnElsewhere()
    {
        await using var host = await TestHost.StartAsync();
        using var tray = Create(host);
        tray.Pause();
        host.Engine.Components.Set(Component.All, true);
        Assert.Null(tray.PausedUntil);
        host.Engine.Components.Set(Component.All, false);
        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.False(host.Engine.Components.IsOn(Component.All));
    }

    [Fact]
    public async Task Resume_EndsThePauseEarly()
    {
        await using var host = await TestHost.StartAsync();
        using var tray = Create(host);
        tray.Pause();
        PauseItem(tray).Invoke!();
        Assert.True(host.Engine.Components.IsOn(Component.All));
        Assert.Null(tray.PausedUntil);
    }

    [Fact]
    public async Task Autostart_Toggles()
    {
        await using var host = await TestHost.StartAsync();
        using var tray = Create(host);
        Item(tray.Build(), "Start with the OS").Invoke!();
        Assert.True(_autostart.IsEnabled);
        Assert.True(Item(tray.Build(), "Start with the OS").Checked);
        Item(tray.Build(), "Start with the OS").Invoke!();
        Assert.False(_autostart.IsEnabled);
    }

    [Fact]
    public async Task Autostart_ThatCantBeChanged_LeavesTheMenuAsItWas()
    {
        await using var host = await TestHost.StartAsync();
        using var tray = Create(host);
        _autostart.Failure = new UnauthorizedAccessException("The Run key is read-only");

        Item(tray.Build(), "Start with the OS").Invoke!();

        Assert.False(_autostart.IsEnabled);
        Assert.False(Item(tray.Build(), "Start with the OS").Checked);
    }
}
