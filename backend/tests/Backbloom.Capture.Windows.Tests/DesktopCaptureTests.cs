using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Backbloom.Capture.Hdr;
using Backbloom.Core.Imaging;
using Backbloom.Core.Tests;

namespace Backbloom.Capture.Windows.Tests;

/// <summary>
/// Captures the real desktop (read-only). Skipped without an interactive desktop with a monitor (CI services, remote
/// sessions without a display). Set BACKBLOOM_CAPTURE_DEVICE to pick a monitor (default "auto") and
/// BACKBLOOM_CAPTURE_DUMP_DIR to save the captured frames as PNG files.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
[Collection(AllocationMeasurements.Name)]
public sealed class DesktopCaptureTests : IDisposable
{
    private static readonly string s_device = Environment.GetEnvironmentVariable("BACKBLOOM_CAPTURE_DEVICE") is { Length: > 0 } d ? d : CaptureSettings.AutoDevice;
    private static readonly Lazy<bool> s_desktopPresents = new(DesktopPresentsFrames);
    private readonly TestLogger<DxgiScreenSource> _log = new();
    private readonly string _dataDir;

    public DesktopCaptureTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && Environment.UserInteractive, "needs an interactive Windows desktop");
        Assert.SkipWhen(MonitorEnumerator.Enumerate().Count == 0, "no monitor attached to the desktop");
        _dataDir = Directory.CreateTempSubdirectory("backbloom-capture-").FullName;
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
        // Windows reports an SDR white level for monitors in HDR mode (80 nits and up; SDR monitors may report one too).
        Assert.All(outputs.Where(o => o.Info.Hdr), o => Assert.InRange(o.SdrWhiteNits, 80, 10000));
        Assert.All(outputs, o => Assert.Equal(o.SdrWhiteNits, o.Target is { } t ? MonitorEnumerator.ReadSdrWhiteNits(t) : 0));
        Assert.Single(MonitorEnumerator.Select(outputs, CaptureSettings.AutoDevice));
        Assert.Equal(outputs[0].Info, MonitorEnumerator.Select(outputs, outputs[0].Info.Id.ToUpperInvariant()).Single().Info);
        if (outputs[0].Info.DevicePath.Length > 0)
        {
            Assert.Equal(outputs[0].Info, MonitorEnumerator.Select(outputs, outputs[0].Info.DevicePath).Single().Info);
        }

        Assert.Empty(MonitorEnumerator.Select(outputs, @"\\.\DISPLAY999|nothing"));
    }

    /// <summary>
    /// Every conversion: the HDR shader (FP16 scRGB on an HDR desktop, else 8-bit SDR), 8-bit on the GPU with and
    /// without the LUT, and the CPU path with and without it. Measured on a dedicated thread after a full collection
    /// (<see cref="Allocations"/>), with the cancellation token's wait handle created beforehand: the frame pacer
    /// creates it lazily on its first wait (56 bytes, 280 when it is the first wait handle after a collection).
    /// </summary>
    [Theory]
    [InlineData(true, 200, false, true)]
    [InlineData(true, 200, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(false, 200, false)]
    [InlineData(false, 200, true)]
    public void Captures_ScaledFrames_WithoutAllocatingPerFrame(bool hardware, int monitorNits, bool toneMapping, bool autoSdrWhite = false)
    {
        RequireFrames();
        var lut = new LutProvider(new LutLocations([_dataDir], _dataDir), NullLogger<LutProvider>.Instance);
        if (toneMapping)
        {
            Assert.NotNull(lut.Get());
        }

        var settings = new CaptureSettings { Device = s_device, Hardware = hardware, MonitorNitsAuto = autoSdrWhite, MonitorNits = monitorNits, ToneMapping = toneMapping ? ToneMappingMode.On : ToneMappingMode.Off, FramesPerSecond = 120, MaxWidth = 512 };
        using var source = new DxgiScreenSource(settings, lut, _log);
        var frame = new Frame(1, 1);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cancel.Token;
        _ = token.WaitHandle;
        var perFrame = new long[60];
        int warmUp = 0, captured = 0;
        CaptureStatistics statistics = default;

        var allocated = Allocations.Measure(
            () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    if (source.TryCapture(frame, token))
                    {
                        warmUp++;
                    }
                }

                source.TakeStatistics();
            },
            () =>
            {
                for (var i = 0; i < perFrame.Length && !token.IsCancellationRequested; i++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    if (source.TryCapture(frame, token))
                    {
                        captured++;
                    }

                    perFrame[i] = GC.GetAllocatedBytesForCurrentThread() - before;
                }

                statistics = source.TakeStatistics();
            });

        Assert.True(warmUp > 0, $"no frame was captured: {_log}");
        Assert.True(captured >= 50, $"only {captured} of {perFrame.Length} frames captured: {_log}");
        Assert.True(allocated == 0, $"{allocated} bytes allocated; per frame: {string.Join(", ", perFrame)}");
        Assert.True(source.IsCapturing);
        Assert.InRange(frame.Width, 1, 512);
        Assert.Equal(source.FrameSize, (frame.Width, frame.Height));
        Assert.All(frame.ReadOnlyPixels.ToArray().Where((_, i) => i % 4 == 3), a => Assert.Equal(255, a));
        Assert.Equal(captured, statistics.Frames);
        TestContext.Current.SendDiagnosticMessage($"{string.Join("; ", source.DescribeDisplays())}: {statistics.NewImages} new images in {captured} frames");
    }

    [Theory]
    [InlineData(ToneMappingMode.Auto)]
    [InlineData(ToneMappingMode.On)]
    [InlineData(ToneMappingMode.Off)]
    public void HdrStatus_DescribesTheCapturedMonitorAndFollowsTheMode(ToneMappingMode mode)
    {
        RequireFrames();
        var lut = new LutProvider(new LutLocations([_dataDir], _dataDir), NullLogger<LutProvider>.Instance);
        using var source = new DxgiScreenSource(new CaptureSettings { Device = s_device, ToneMapping = mode, FramesPerSecond = 120 }, lut, _log);
        var changes = new List<CaptureHdrStatus>();
        source.HdrStatusChanged += changes.Add;
        Assert.Equal(CaptureHdrStatus.Idle(mode), source.HdrStatus);

        Assert.True(CaptureFrames(source, new Frame(1, 1), 3) > 0, _log.ToString());

        var monitor = MonitorEnumerator.Select(MonitorEnumerator.Enumerate(), s_device).Single();
        var hdr = monitor.Info.Hdr;
        // An HDR monitor is captured in HDR (hardware on, Windows' SDR white level or 200 nits) and converted by the
        // shader in every mode; an SDR monitor gets the table only when it is forced on.
        var sdrWhite = hdr ? (monitor.SdrWhiteNits > 0 ? monitor.SdrWhiteNits : 200) : 0;
        var expected = new CaptureHdrStatus(mode, Capturing: true, MonitorHdr: hdr, ToneMapped: hdr || mode == ToneMappingMode.On, sdrWhite);
        Assert.Equal(expected, source.HdrStatus);
        Assert.Equal([expected], changes);
        Assert.Equal(mode == ToneMappingMode.On && !hdr, source.DescribeDisplays().Single().Contains("+ LUT", StringComparison.Ordinal));
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
        // an unknown adapter: an unknown single monitor would be the only connected one here (MonitorSelectionTests)
        using var source = CreateSource(new CaptureSettings { Device = "MULTI-MONITOR|nothing" });
        // Start timing before the cancellation timer so a slow start can't shorten the measured wait.
        var watch = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Assert.False(source.TryCapture(new Frame(1, 1), cancel.Token));
        Assert.False(source.TryCapture(new Frame(1, 1), cancel.Token));

        Assert.False(source.IsCapturing);
        Assert.Equal("none", source.Device);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), "the retry must wait instead of spinning");
    }

    /// <summary>Saves frames of each conversion for a visual check (only with BACKBLOOM_CAPTURE_DUMP_DIR).</summary>
    [Fact]
    public void DumpFrames_ForVisualInspection()
    {
        var dir = Environment.GetEnvironmentVariable("BACKBLOOM_CAPTURE_DUMP_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "set BACKBLOOM_CAPTURE_DUMP_DIR to save frames");
        RequireFrames();
        Directory.CreateDirectory(dir!);
        var variants = new (string Name, CaptureSettings Settings)[]
        {
            ("gpu-hdr-200nits", new CaptureSettings { Device = s_device, MonitorNitsAuto = false, MonitorNits = 200 }),
            ("gpu-hdr-100nits", new CaptureSettings { Device = s_device, MonitorNitsAuto = false, MonitorNits = 100 }),
            ("gpu-sdr-windows", new CaptureSettings { Device = s_device, MonitorNitsAuto = false, MonitorNits = 0 }),
            ("gpu-sdr-lut", new CaptureSettings { Device = s_device, MonitorNitsAuto = false, MonitorNits = 0, ToneMapping = ToneMappingMode.On }),
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
        var dir = Directory.CreateTempSubdirectory("backbloom-capture-probe-").FullName;
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
