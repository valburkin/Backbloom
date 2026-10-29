using System.Runtime.Versioning;

namespace Backbloom.Capture.Windows.Tests;

/// <summary>Picking the monitors for a device setting, on made-up monitors (no desktop needed).</summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class MonitorSelectionTests
{
    private const string Gpu = "NVIDIA GeForce RTX 5080";
    private const string LgPath = @"\\?\DISPLAY#GSM7840#5&22fca7dd&0&UID8449#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string DellPath = @"\\?\DISPLAY#DEL4321#5&22fca7dd&0&UID8450#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static OutputLocation Output(int display, string path, uint output = 0, bool primary = true) =>
        new(0, output, new MonitorInfo($@"\\.\DISPLAY{display}|{Gpu}", path, "Monitor", Gpu, 3440, 1440, false), primary);

    [Fact]
    public void AMonitorFoundByItsName_IsRemembered()
    {
        var selected = MonitorEnumerator.SelectFor([Output(5, LgPath)], $@"\\.\DISPLAY5|{Gpu}", null, out var match);

        Assert.Equal(LgPath, Assert.Single(selected).Info.DevicePath);
        Assert.Equal(MonitorMatch.Setting, match);
        Assert.Equal(LgPath, MonitorEnumerator.PathToRemember(selected, match));
    }

    [Fact]
    public void AfterADriverUpdate_TheMonitorCapturedBefore_IsFoundUnderItsNewName()
    {
        // seen 2026-10-27: an NVIDIA driver update brought DISPLAY5 back as DISPLAY9
        var selected = MonitorEnumerator.SelectFor([Output(9, LgPath)], $@"\\.\DISPLAY5|{Gpu}", LgPath, out var match);

        Assert.Equal($@"\\.\DISPLAY9|{Gpu}", Assert.Single(selected).Info.Id);
        Assert.Equal(MonitorMatch.Renamed, match);
        Assert.Equal(LgPath, MonitorEnumerator.PathToRemember(selected, match));
    }

    [Fact]
    public void AfterARestartWithANewName_TheOnlyMonitorIsCaptured_ButNotRemembered()
    {
        var selected = MonitorEnumerator.SelectFor([Output(9, LgPath)], $@"\\.\DISPLAY5|{Gpu}", null, out var match);

        Assert.Equal($@"\\.\DISPLAY9|{Gpu}", Assert.Single(selected).Info.Id);
        Assert.Equal(MonitorMatch.OnlyMonitor, match);
        Assert.Null(MonitorEnumerator.PathToRemember(selected, match));
    }

    [Fact]
    public void WithSeveralMonitors_TheOneCapturedBeforeIsFoundByItsDevicePath_EvenWhenAnotherTookItsName()
    {
        IReadOnlyList<OutputLocation> outputs = [Output(5, DellPath, 0), Output(9, LgPath, 1, primary: false)];

        var selected = MonitorEnumerator.SelectFor(outputs, $@"\\.\DISPLAY5|{Gpu}", LgPath, out var match);

        Assert.Equal(LgPath, Assert.Single(selected).Info.DevicePath);
        Assert.Equal(MonitorMatch.Renamed, match);
    }

    [Fact]
    public void AMonitorThatIsAway_IsWaitedFor_NotReplacedByTheRemainingOne()
    {
        // the TV (DISPLAY2) is switched off and leaves Windows' list, then comes back: capture must go back to the TV
        var setting = $@"\\.\DISPLAY2|{Gpu}";
        var desk = Output(1, DellPath, 0);
        var tv = Output(2, LgPath, 1, primary: false);
        var first = MonitorEnumerator.SelectFor([desk, tv], setting, null, out var match);
        var remembered = MonitorEnumerator.PathToRemember(first, match);

        Assert.Empty(MonitorEnumerator.SelectFor([desk], setting, remembered, out match));
        Assert.Equal(LgPath, Assert.Single(MonitorEnumerator.SelectFor([desk, tv], setting, remembered, out match)).Info.DevicePath);
        Assert.Equal(MonitorMatch.Setting, match);
    }

    [Fact]
    public void WithSeveralMonitors_AndNothingKnown_NoMonitorIsGuessed()
    {
        IReadOnlyList<OutputLocation> outputs = [Output(8, DellPath, 0), Output(9, LgPath, 1, primary: false)];

        Assert.Empty(MonitorEnumerator.SelectFor(outputs, $@"\\.\DISPLAY5|{Gpu}", null, out var match));
        Assert.Equal(MonitorMatch.Setting, match);
    }

    [Fact]
    public void AutoAndMultiMonitor_IgnoreTheLastPath()
    {
        IReadOnlyList<OutputLocation> outputs = [Output(5, DellPath, 0), Output(9, LgPath, 1, primary: false)];

        Assert.Equal(DellPath, Assert.Single(MonitorEnumerator.SelectFor(outputs, CaptureSettings.AutoDevice, LgPath, out _)).Info.DevicePath);
        Assert.Equal(2, MonitorEnumerator.SelectFor(outputs, $"MULTI-MONITOR|{Gpu}", LgPath, out var match).Count);
        Assert.Equal(MonitorMatch.Setting, match);
        Assert.Empty(MonitorEnumerator.SelectFor([Output(9, LgPath)], "MULTI-MONITOR|another adapter", null, out _));
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1000u, 80)]
    [InlineData(2500u, 200)]
    [InlineData(3010u, 241)]
    public void SdrWhiteLevel_IsInThousandthsOf80Nits(uint level, int nits) =>
        Assert.Equal(nits, MonitorEnumerator.SdrWhiteLevelToNits(level));
}
