using System.Numerics;
using Backbloom.Core.Color;
using Backbloom.Core.Layout;
using Backbloom.Core.Mapping;
using Backbloom.Core.Smoothing;

namespace Backbloom.Core.Tests;

[Collection(AllocationMeasurements.Name)]
public sealed class HotPathAllocationTests
{
    private static object? s_kept;

    [Fact]
    public void Measurement_SeesEvenASmallAllocation()
    {
        Assert.Equal(0, Allocations.Measure(() => { }, () => { }));
        Assert.InRange(Allocations.Measure(() => { }, () => s_kept = new byte[1]), 25, 64);
        GC.KeepAlive(s_kept);
    }

    [Fact]
    public void MappingProcessingAndQuantization_DoNotAllocatePerFrame()
    {
        // the owner's layout plus a few "Screen average" LEDs
        var owner = LedLayout.FromJson(Fixtures.Load("layouts/owner-168.json"));
        var layout = new LedLayout([.. owner.Areas, new LedArea(0, 1, 0, 1), new LedArea(0, 1, 0, 1)]);
        var frame = TestFrames.Solid(512, 288, 30, 120, 220);
        var options = new LedMappingOptions(layout);
        var mapper = new LedMapper();
        var screenAverage = new ScreenAverageFilter();
        long now = 0;
        var processor = new ColorProcessor(new ColorAdjustment
        {
            Temperature = TemperaturePreset.Warm,
            Red = new Rgb8(250, 5, 5),
            ScaleOutput = 0.8f,
            LuminanceGain = 1.1f,
            BacklightThreshold = 0.01f,
            PowerLimit = 0.5f,
        });
        var colors = new Vector3[layout.Count];
        var bytes = new byte[layout.Count * 3];

        void RunFrame()
        {
            mapper.Process(frame, options, colors);
            screenAverage.Apply(layout, colors, now += 16);
            processor.Process(colors);
            ColorQuantizer.ToBytes(colors, ColorOrder.Grb, bytes);
        }

        // Warm up past tiered-JIT promotion so recompilation cannot allocate inside the measured window.
        var allocated = Allocations.Measure(
            () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    RunFrame();
                }
            },
            () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    RunFrame();
                }
            });
        Assert.Equal(0, allocated);
    }
}
