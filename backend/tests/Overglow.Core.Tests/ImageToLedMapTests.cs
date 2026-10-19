using System.Numerics;
using System.Text.Json.Nodes;
using Overglow.Core.Color;
using Overglow.Core.Layout;
using Overglow.Core.Mapping;

namespace Overglow.Core.Tests;

public sealed class ImageToLedMapTests
{
    private static float Linear(byte value) => Srgb.U8ToLinear16[value] / 65535.0f;

    [Fact]
    public void Process_SolidFrame_EveryLedGetsTheLinearColor()
    {
        var frame = TestFrames.Solid(64, 36, 200, 100, 10);
        var layout = LedLayout.FromJson(Fixtures.Load("layouts/owner-168.json"));
        var map = new ImageToLedMap(layout, LedMappingType.Advanced, false, frame.Width, frame.Height);
        var colors = new Vector3[layout.Count];

        map.Process(frame.ReadOnlyPixels, colors);

        var expected = new Vector3(Linear(200), Linear(100), Linear(10));
        Assert.All(colors, c => Assert.Equal(expected, c));
    }

    [Fact]
    public void Process_AveragesInLinearLight()
    {
        // Columns 0..9 have red = 20 * x. An LED over 0.25..0.75 samples columns 3..7 (qRound(2.5) = 3, qRound(7.5) = 8).
        var frame = TestFrames.Solid(10, 2, 0, 0, 0);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                TestFrames.SetPixel(frame, x, y, (byte)(20 * x), 0, 0);
            }
        }

        var map = new ImageToLedMap(new LedLayout([new LedArea(0.25, 0.75, 0, 1)]), LedMappingType.Advanced, false, 10, 2);
        var colors = new Vector3[1];

        map.Process(frame.ReadOnlyPixels, colors);

        long sum = 0;
        for (var x = 3; x <= 7; x++)
        {
            sum += Srgb.U8ToLinear16[20 * x];
        }

        Assert.Equal((float)(sum * 2) / 10 / 65535.0f, colors[0].X);
        Assert.Equal(10, map.SampledPixels);
    }

    [Fact]
    public void Process_ThinAreaStillSamplesOnePixel()
    {
        var frame = TestFrames.Solid(10, 10, 0, 0, 0);
        TestFrames.SetPixel(frame, 9, 9, 255, 255, 255);
        var map = new ImageToLedMap(new LedLayout([new LedArea(0.99, 1.0, 0.99, 1.0)]), LedMappingType.Advanced, false, 10, 10);
        var colors = new Vector3[1];

        map.Process(frame.ReadOnlyPixels, colors);

        Assert.Equal(Vector3.One, colors[0]);
        Assert.Equal(1, map.SampledPixels);
    }

    [Fact]
    public void Process_EmptyAreaIsBlack()
    {
        var frame = TestFrames.Solid(8, 8, 255, 255, 255);
        var map = new ImageToLedMap(new LedLayout([new LedArea(0.5, 0.5, 0, 1)]), LedMappingType.Advanced, false, 8, 8);
        var colors = new Vector3[] { Vector3.One };

        map.Process(frame.ReadOnlyPixels, colors);

        Assert.Equal(Vector3.Zero, colors[0]);
    }

    [Fact]
    public void Process_UnicolorMean_AllLedsGetTheFrameAverage()
    {
        var frame = TestFrames.Solid(4, 2, 0, 0, 0);
        TestFrames.SetPixel(frame, 0, 0, 255, 0, 0);
        var layout = new LedLayout([new LedArea(0.5, 1, 0, 1), new LedArea(0, 0.5, 0, 1, Disabled: true)]);
        var map = new ImageToLedMap(layout, LedMappingType.UnicolorMean, false, 4, 2);
        var colors = new Vector3[2];

        map.Process(frame.ReadOnlyPixels, colors);

        var expected = new Vector3(65535.0f / 8 / 65535.0f, 0, 0);
        Assert.Equal(expected, colors[0]);
        Assert.Equal(expected, colors[1]);
    }

    [Fact]
    public void Process_UnicolorMeanSparse_SamplesEveryOtherPixel()
    {
        var frame = TestFrames.Solid(4, 4, 0, 0, 0);
        TestFrames.SetPixel(frame, 1, 0, 255, 255, 255);
        var map = new ImageToLedMap(new LedLayout([new LedArea(0, 1, 0, 1)]), LedMappingType.UnicolorMean, true, 4, 4);
        var colors = new Vector3[1];

        map.Process(frame.ReadOnlyPixels, colors);

        Assert.Equal(Vector3.Zero, colors[0]);
    }

    [Fact]
    public void Constructor_LargeAreaSwitchesToSparseSampling()
    {
        var layout = new LedLayout([new LedArea(0, 1, 0, 1), new LedArea(0, 0.1, 0, 0.1)]);

        var map = new ImageToLedMap(layout, LedMappingType.Advanced, false, 100, 100);

        Assert.Equal([0], map.AutoSparseLeds);
        Assert.Equal((50 * 50) + 100, map.SampledPixels);
    }

    [Fact]
    public void Constructor_HonorsBlackBorders()
    {
        // With a 2 px vertical border on a 10 px wide frame, x = 0..1 maps to columns 2..7.
        var frame = TestFrames.Solid(10, 1, 0, 0, 0);
        TestFrames.SetPixel(frame, 2, 0, 255, 0, 0);
        var map = new ImageToLedMap(new LedLayout([new LedArea(0, 1.0 / 6, 0, 1)]), LedMappingType.Advanced, false, 10, 1, 0, 2);
        var colors = new Vector3[1];

        map.Process(frame.ReadOnlyPixels, colors);

        Assert.Equal(new Vector3(1, 0, 0), colors[0]);
    }

    [Fact]
    public void Constructor_RejectsBordersCoveringTheFrame() =>
        Assert.Throws<ArgumentException>(() => new ImageToLedMap(LedLayout.Empty, LedMappingType.Advanced, false, 10, 10, 5, 0));

    [Fact]
    public void LedMapper_RebuildsOnlyWhenSizeOrOptionsChange()
    {
        var mapper = new LedMapper();
        var rebuilt = 0;
        mapper.MapRebuilt += _ => rebuilt++;
        var options = new LedMappingOptions(LedLayout.FromJson(JsonNode.Parse("""[{"hmin":0,"hmax":1,"vmin":0,"vmax":1}]""")));
        var colors = new Vector3[1];

        mapper.Process(TestFrames.Solid(8, 8, 1, 2, 3), options, colors);
        mapper.Process(TestFrames.Solid(8, 8, 1, 2, 3), options, colors);
        mapper.Process(TestFrames.Solid(16, 8, 1, 2, 3), options, colors);
        mapper.Process(TestFrames.Solid(16, 8, 1, 2, 3), options with { MappingType = LedMappingType.UnicolorMean }, colors);

        Assert.Equal(3, rebuilt);
    }
}
