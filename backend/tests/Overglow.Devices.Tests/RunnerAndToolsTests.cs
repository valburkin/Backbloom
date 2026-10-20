using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Overglow.Core.Color;
using Overglow.Devices.Drivers.File;
using Overglow.Devices.Rgbw;

namespace Overglow.Devices.Tests;

/// <summary>Runner behavior added for the new drivers (float colors, lost devices, driver refresh), the driver tools and the file driver's deep color output.</summary>
public sealed class RunnerAndToolsTests
{
    [Fact]
    public void Runner_ReopensALostDevice()
    {
        var driver = new LosingDriver();
        var runner = new LedDeviceRunner(driver, new LedDeviceRunnerOptions("lost", 1), NullLogger.Instance);
        var states = new ConcurrentQueue<DeviceState>();
        runner.StateChanged += (_, s) => states.Enqueue(s);
        using (runner)
        {
            runner.Start();
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            driver.LoseNext = true;
            runner.Submit([Vector3.One]);
            Wait.Until(() => driver.Opens == 2 && runner.State == DeviceState.Ready);
        }

        Assert.Contains(DeviceState.WaitingToRetry, states);
    }

    [Fact]
    public void Runner_SendsFloatColorsInRgbOrderToFloatDrivers()
    {
        var driver = new FloatDriver();
        var runner = new LedDeviceRunner(driver, new LedDeviceRunnerOptions("float", 1, ColorOrder.Grb, TimeSpan.FromMilliseconds(500)), NullLogger.Instance);
        using (runner)
        {
            runner.Start();
            runner.SetEnabled(true);
            Wait.Until(() => runner.State == DeviceState.Ready);
            runner.Submit([new Vector3(1, 0.5f, 0)]);
            Wait.Until(() => !driver.Frames.IsEmpty);
        }

        Assert.True(driver.Frames.TryDequeue(out var frame));
        Assert.Equal((new Vector3(1, 0.5f, 0), TimeSpan.FromMilliseconds(40)), frame);
    }

    [Fact]
    public async Task Tools_AnswerInTheCppShape()
    {
        var registry = new DriverRegistry().RegisterAll();
        var tools = new DeviceTools(registry, Ctx.Create());

        Assert.Equal("""{"ledDeviceType":"adalight","devices":[{"value":"auto","name":"Auto"}]}""", (await tools.DiscoverAsync("adalight", null, CancellationToken.None)).ToJsonString());
        Assert.Equal("""{"ledDeviceType":"nope","devices":[]}""", (await tools.DiscoverAsync("nope", null, CancellationToken.None)).ToJsonString());
        Assert.Equal("""{"properties":{}}""", (await tools.GetPropertiesAsync("file", null, CancellationToken.None)).ToJsonString());
        Assert.False(await tools.IdentifyAsync("file", null, CancellationToken.None));
        Assert.Equal(16, registry.Drivers.Count);
        Assert.All(registry.Drivers, d => Assert.Equal("object", d.Schema["type"]!.GetValue<string>()));
    }

    [Fact]
    public void File_FormatsDeepColorsAndRgbw()
    {
        var line = new char[256];
        var time = new DateTime(2026, 10, 19, 20, 15, 0, 123);

        var n = FileDriver.FormatFloatLine(line, time, TimeSpan.FromMilliseconds(16.66), [new Vector3(1, 0.5f, 0.049f), Vector3.Zero]);
        Assert.Equal("2026-10-19T20:15:00.123 | +16.7 [{255.0000, 127.5000,  12.4950}, {  0.0000,   0.0000,   0.0000}]" + Environment.NewLine, new string(line, 0, n));

        n = FileDriver.FormatRgbwLine(line, null, TimeSpan.Zero, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(" [{1,2,3,4}, {5,6,7,8}]" + Environment.NewLine, new string(line, 0, n));
    }

    [Fact]
    public async Task File_WritesRgbwFromTheInfiniteColorEngine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"overglow-file-{Guid.NewGuid():N}.txt");
        try
        {
            var options = new FileDriverOptions(path, PrintTimeStamp: false, Rgbw: new InfiniteRgbwOptions(true, 0, 1, Vector3.One));
            using (var driver = new FileDriver(options))
            {
                await driver.OpenAsync(1, TestContext.Current.CancellationToken);
                Assert.True(driver.WritesFloatColors);
                driver.WriteFloat([new Vector3(0, 0, 1)], TimeSpan.Zero);
                await driver.CloseAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(" [{0,0,255,0}]" + Environment.NewLine, await System.IO.File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private sealed class LosingDriver : ILedDriver
    {
        public bool LoseNext { get; set; }

        public int Opens { get; private set; }

        public string Type => "losing";

        public RetryPolicy RetryPolicy { get; } = new(3, TimeSpan.FromMilliseconds(10));

        public string? Status => null;

        public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken)
        {
            Opens++;
            return ValueTask.CompletedTask;
        }

        public bool Write(ReadOnlySpan<byte> colors)
        {
            if (LoseNext)
            {
                LoseNext = false;
                throw new DeviceLostException("unplugged");
            }

            return true;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FloatDriver : IFloatColorDriver
    {
        public ConcurrentQueue<(Vector3 Color, TimeSpan Interval)> Frames { get; } = new();

        public string Type => "float";

        public RetryPolicy RetryPolicy => RetryPolicy.None;

        public string? Status => null;

        public bool WritesFloatColors => true;

        public TimeSpan? EffectiveRefreshInterval(TimeSpan? configured) => TimeSpan.FromMilliseconds(40);

        public ValueTask OpenAsync(int ledCount, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public bool Write(ReadOnlySpan<byte> colors) => throw new InvalidOperationException("bytes are not expected");

        public bool WriteFloat(ReadOnlySpan<Vector3> colors, TimeSpan frameInterval)
        {
            if (colors[0] != Vector3.Zero)
            {
                Frames.Enqueue((colors[0], frameInterval));
            }

            return true;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Dispose()
        {
        }
    }
}
