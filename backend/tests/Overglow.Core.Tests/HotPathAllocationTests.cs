using System.Numerics;
using Overglow.Core.Color;
using Overglow.Core.Layout;
using Overglow.Core.Mapping;

namespace Overglow.Core.Tests;

public sealed class HotPathAllocationTests
{
    [Fact]
    public void MappingProcessingAndQuantization_DoNotAllocatePerFrame()
    {
        var layout = LedLayout.FromJson(Fixtures.Load("layouts/owner-168.json"));
        var frame = TestFrames.Solid(512, 288, 30, 120, 220);
        var options = new LedMappingOptions(layout);
        var mapper = new LedMapper();
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
            processor.Process(colors);
            ColorQuantizer.ToBytes(colors, ColorOrder.Grb, bytes);
        }

        // Warm up past tiered-JIT promotion so recompilation cannot allocate inside the measured window.
        for (var i = 0; i < 100; i++)
        {
            RunFrame();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            RunFrame();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
