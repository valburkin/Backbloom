using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Capture;
using Overglow.Core.Color;
using Overglow.Core.Layout;
using Overglow.Core.Mapping;
using Overglow.Core.Priorities;
using Overglow.Core.Smoothing;
using Overglow.Devices;
using Overglow.Engine.Platform;

namespace Overglow.Engine.Tests;

/// <summary>
/// CPU cost of the whole pipeline with smoothing: the owner's 168 LEDs, the synthetic source at 60 fps, smoothing at
/// 50 Hz and four file controllers writing to the null device. Explicit: run with
/// <c>dotnet test -c Release --filter SmoothingProfile -- xUnit.Explicit=on</c>; the result is written to the output.
/// </summary>
public sealed class SmoothingProfile(ITestOutputHelper output)
{
    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void Cpu_At50HzWithFourControllers(bool smoothing)
    {
        var registry = new DriverRegistry().RegisterAll();
        using var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        var device = JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": true }""")!.AsObject();
        devices.Configure([device, device.DeepClone().AsObject(), device.DeepClone().AsObject(), device.DeepClone().AsObject()], 168);
        devices.SetEnabled(true);
        var layout = LedLayout.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "layouts", "owner-168.json"))));
        var feed = new LedColorsFeed();
        using var clock = OperatingSystem.IsWindows() ? new Overglow.Platform.Windows.WindowsClock() : null;
        using var stage = new OutputStage(devices, feed, (IClock?)clock ?? new SystemClock(), NullLogger<OutputStage>.Instance);
        stage.Configure(SmoothingSettings.FromJson(JsonNode.Parse($$"""
            { "enable": {{(smoothing ? "true" : "false")}}, "type": "HybridRgbInterpolator", "time_ms": 150, "updateFrequency": 50,
              "stiffness": 150, "damping": 26, "antiFlickeringFilter": true, "continuousOutput": true }
            """)));
        var screen = new VisibleSource(245, SourceComponent.SystemGrabber, Rgb8.Black, null, 0, 1);
        using var pipeline = new LightingPipeline(
            new SyntheticFrameSource(new SyntheticFrameSourceOptions(FramesPerSecond: 60)),
            new ComponentStates(),
            () => screen,
            stage,
            new PipelineConfig(new LedMappingOptions(layout), new ColorProcessor(new ColorAdjustment())),
            NullLogger<LightingPipeline>.Instance);
        stage.Start();
        pipeline.Start();
        Thread.Sleep(2000);

        using var process = Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var frames0 = pipeline.FrameCount;
        var ticks0 = stage.Ticks;
        var written0 = devices.Devices.Sum(d => d.Stats.Written);
        var sw = Stopwatch.StartNew();
        Thread.Sleep(10_000);
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds * 100;
        var seconds = sw.Elapsed.TotalSeconds;
        output.WriteLine(
            $"smoothing {smoothing}: {cpu:0.00} % of one core, {(pipeline.FrameCount - frames0) / seconds:0.0} frames/s, " +
            $"{(stage.Ticks - ticks0) / seconds:0.0} ticks/s, {(devices.Devices.Sum(d => d.Stats.Written) - written0) / seconds:0.0} writes/s (4 controllers)");
    }
}
