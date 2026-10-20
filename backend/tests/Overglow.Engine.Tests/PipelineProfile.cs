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
/// CPU cost of the whole pipeline: the owner's 168 LEDs on one file controller writing to the null device, or the
/// same plus two 30-LED controllers (three controllers, one mapping pass), the synthetic source at 60 fps, with or
/// without smoothing at 50 Hz. Explicit: run with
/// <c>dotnet test -c Release --filter PipelineProfile -- xUnit.Explicit=on</c>; the result is written to the output.
/// </summary>
public sealed class PipelineProfile(ITestOutputHelper output)
{
    [Theory(Explicit = true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void Cpu_At60Fps(int controllers, bool smoothing)
    {
        var registry = new DriverRegistry().RegisterAll();
        using var devices = new DeviceManager(registry, new DriverContext(NullLoggerFactory.Instance, new HttpClient()), NullLoggerFactory.Instance);
        JsonObject Device() => JsonNode.Parse("""{ "type": "file", "output": "/dev/null", "printTimeStamp": true }""")!.AsObject();
        List<ControllerDevice> list = [new("a", "Monitor and ceiling", Device(), 168)];
        var owner = LedLayout.FromJson(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "layouts", "owner-168.json"))));
        var areas = owner.Areas.ToList();
        for (var i = 1; i < controllers; i++)
        {
            list.Add(new ControllerDevice($"c{i}", $"Test strip {i}", Device(), 30));
            areas.AddRange(Enumerable.Range(0, 30).Select(k => new LedArea(k / 30.0, (k + 1) / 30.0, 0.4, 0.6, 0, false)));
        }

        devices.Configure(list);
        devices.SetEnabled(true);
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
            new PipelineConfig(new LedMappingOptions(new LedLayout(areas)), new ColorProcessor(new ColorAdjustment())),
            NullLogger<LightingPipeline>.Instance);
        stage.Start();
        pipeline.Start();
        Thread.Sleep(2000);

        using var process = Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var frames0 = pipeline.FrameCount;
        var ticks0 = stage.Ticks;
        var written0 = devices.Devices.Select(d => d.Stats.Written).ToList();
        var sw = Stopwatch.StartNew();
        Thread.Sleep(10_000);
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds * 100;
        var seconds = sw.Elapsed.TotalSeconds;
        var rates = devices.Devices.Select((d, i) => $"{(d.Stats.Written - written0[i]) / seconds:0.0}");
        output.WriteLine(
            $"{controllers} controller(s), smoothing {smoothing}: {cpu:0.00} % of one core, {(pipeline.FrameCount - frames0) / seconds:0.0} frames/s, " +
            $"{(stage.Ticks - ticks0) / seconds:0.0} ticks/s, writes/s per controller {string.Join(" / ", rates)}");
    }
}
