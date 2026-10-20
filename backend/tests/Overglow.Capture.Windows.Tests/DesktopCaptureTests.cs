using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Capture.Hdr;
using Overglow.Core.Imaging;

namespace Overglow.Capture.Windows.Tests;

/// <summary>
/// Captures the real desktop (read-only). Skipped without an interactive desktop with a monitor (CI services, remote
/// sessions without a display). Set OVERGLOW_CAPTURE_DEVICE to pick a monitor (default "auto") and
/// OVERGLOW_CAPTURE_DUMP_DIR to save the captured frames as PNG files.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public sealed class DesktopCaptureTests : IDisposable
{
    private static readonly string s_device = Environment.GetEnvironmentVariable("OVERGLOW_CAPTURE_DEVICE") is { Length: > 0 } d ? d : CaptureSettings.AutoDevice;
    private static readonly Lazy<bool> s_desktopPresents = new(DesktopPresentsFrames);
    private readonly TestLogger<DxgiScreenSource> _log = new();
    private readonly string _dataDir;

    public DesktopCaptureTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && Environment.UserInteractive, "needs an interactive Windows desktop");
        Assert.SkipWhen(MonitorEnumerator.Enumerate().Count == 0, "no monitor attached to the desktop");
        _dataDir = Directory.CreateTempSubdirectory("overglow-capture-").FullName;
    }

    public void Dispose() => Directory.Delete(_dataDir, recursive: true);

    [Fact]
    public void Monitors_HaveIdsNamesAndSizes()
    {
        var outputs = MonitorEnumerator.Enumerate();
        Assert.All(outputs, o =>
        {
            Assert.Matches(@"^\\\\\.\\DISPLAY\d+\|.+", o.Info.Id);
            Assert.NotEmpty(o.Info.FriendlyName);
            Assert.True(o.Info.Width > 0 && o.Info.Height > 0);
        });
        Assert.Single(MonitorEnumerator.Select(outputs, CaptureSettings.AutoDevice));
        Assert.Equal(outputs[0].Info, MonitorEnumerator.Select(outputs, outputs[0].Info.Id.ToUpperInvariant()).Single().Info);
        if (outputs[0].Info.DevicePath.Length > 0)
        {
            Assert.Equal(outputs[0].Info, MonitorEnumerator.Select(outputs, outputs[0].Info.DevicePath).Single().Info);
        }

        Assert.Empty(MonitorEnumerator.Select(outputs, @"\\.\DISPLAY999|nothing"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Captures_ScaledFrames_WithoutAllocatingPerFrame(bool hardware)
    {
        RequireFrames();
        using var source = CreateSource(new CaptureSettings { Device = s_device, Hardware = hardware, FramesPerSecond = 120, MaxWidth = 512 });
        var frame = new Frame(1, 1);

        var first = CaptureFrames(source, frame, 5);
        Assert.True(first > 0, $"no frame was captured: {_log}");
        Assert.True(source.IsCapturing);
        Assert.InRange(frame.Width, 1, 512);
        Assert.Equal(source.FrameSize, (frame.Width, frame.Height));
        Assert.All(frame.ReadOnlyPixels.ToArray().Where((_, i) => i % 4 == 3), a => Assert.Equal(255, a));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var captured = CaptureFrames(source, frame, 60);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(captured >= 50, $"only {captured} of 60 frames captured");
        Assert.Equal(0, allocated);
        var statistics = source.TakeStatistics();
        Assert.True(statistics.Frames >= captured);
    }

    [Fact]
    public void Reconfigure_RestartsWithTheNewSettings()
    {
        RequireFrames();
        using var source = CreateSource(new CaptureSettings { Device = s_device, MaxWidth = 512, FramesPerSecond = 120 });
        var frame = new Frame(1, 1);
        Assert.True(CaptureFrames(source, frame, 3) > 0, _log.ToString());
        var large = frame.Width;

        source.Configure(new CaptureSettings { Device = s_device, MaxWidth = 256, FramesPerSecond = 120 });
        Assert.True(CaptureFrames(source, frame, 3) > 0, _log.ToString());

        Assert.True(frame.Width <= 256 && frame.Width < large, $"{frame.Width} after {large}");
    }

    [Fact]
    public void UnknownMonitor_IsRetriedWithoutFrames()
    {
        using var source = CreateSource(new CaptureSettings { Device = @"\\.\DISPLAY999|nothing" });
        // Start timing before the cancellation timer so a slow start can't shorten the measured wait.
        var watch = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Assert.False(source.TryCapture(new Frame(1, 1), cancel.Token));
        Assert.False(source.TryCapture(new Frame(1, 1), cancel.Token));

        Assert.False(source.IsCapturing);
        Assert.Equal("none", source.Device);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), "the retry must wait instead of spinning");
    }

    /// <summary>Saves frames of each conversion for a visual check (only with OVERGLOW_CAPTURE_DUMP_DIR).</summary>
    [Fact]
    public void DumpFrames_ForVisualInspection()
    {
        var dir = Environment.GetEnvironmentVariable("OVERGLOW_CAPTURE_DUMP_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "set OVERGLOW_CAPTURE_DUMP_DIR to save frames");
        RequireFrames();
        Directory.CreateDirectory(dir!);
        var variants = new (string Name, CaptureSettings Settings)[]
        {
            ("gpu-hdr-200nits", new CaptureSettings { Device = s_device, MonitorNits = 200 }),
            ("gpu-hdr-100nits", new CaptureSettings { Device = s_device, MonitorNits = 100 }),
            ("gpu-sdr-windows", new CaptureSettings { Device = s_device, MonitorNits = 0 }),
            ("gpu-sdr-lut", new CaptureSettings { Device = s_device, MonitorNits = 0, HdrToneMapping = true }),
            ("cpu-sdr-windows", new CaptureSettings { Device = s_device, Hardware = false }),
        };
        var lut = new LutProvider(new LutLocations([_dataDir], _dataDir), NullLogger<LutProvider>.Instance);
        Assert.NotNull(lut.Get());
        foreach (var (name, settings) in variants)
        {
            using var source = new DxgiScreenSource(settings with { FramesPerSecond = 30 }, lut, NullLogger<DxgiScreenSource>.Instance);
            var frame = new Frame(1, 1);
            Assert.True(CaptureFrames(source, frame, 3) > 0, name);
            Png.Write(Path.Combine(dir!, $"capture-frame-{name}.png"), frame);
            TestContext.Current.SendDiagnosticMessage($"{name}: {string.Join("; ", source.DescribeDisplays())}");
        }
    }

    /// <summary>Skips when the desktop delivers no image, e.g. while the monitor is off (DWM presents nothing then).</summary>
    private static void RequireFrames() =>
        Assert.SkipUnless(s_desktopPresents.Value, "the desktop delivers no frames (monitor off or in standby?)");

    private static bool DesktopPresentsFrames()
    {
        var dir = Directory.CreateTempSubdirectory("overglow-capture-probe-").FullName;
        try
        {
            using var source = new DxgiScreenSource(
                new CaptureSettings { Device = s_device, FramesPerSecond = 30 },
                new LutProvider(new LutLocations([dir], dir), NullLogger<LutProvider>.Instance),
                NullLogger<DxgiScreenSource>.Instance);
            return CaptureFrames(source, new Frame(1, 1), 10) > 0;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int CaptureFrames(DxgiScreenSource source, Frame frame, int count)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var captured = 0;
        for (var i = 0; i < count && !cancel.IsCancellationRequested; i++)
        {
            if (source.TryCapture(frame, cancel.Token))
            {
                captured++;
            }
        }

        return captured;
    }

    private DxgiScreenSource CreateSource(CaptureSettings settings) =>
        new(settings, new LutProvider(new LutLocations([_dataDir], _dataDir), NullLogger<LutProvider>.Instance), _log);
}
