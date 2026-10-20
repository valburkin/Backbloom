using System.Text.Json.Nodes;
using Overglow.Core.Borders;
using Overglow.Core.Imaging;

namespace Overglow.Core.Tests;

/// <summary>
/// The consistency rules of BlackBorderProcessor::updateBorder (the C++ processor needs a LightingInstance, so there
/// is no exported fixture for it; the detector itself is covered by the golden test).
/// </summary>
[Collection(AllocationMeasurements.Name)]
public sealed class BlackBorderProcessorTests
{
    private const int Width = 64;
    private const int Height = 36;

    private static readonly Frame s_letterbox6 = Bars(6);
    private static readonly Frame s_letterbox3 = Bars(3);
    private static readonly Frame s_black = TestFrames.Solid(Width, Height, 0, 0, 0);

    // a lit picture with black rows at the top and the bottom
    private static Frame Bars(int rows)
    {
        var frame = TestFrames.Solid(Width, Height, 200, 150, 100);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                TestFrames.SetPixel(frame, x, y, 0, 0, 0);
                TestFrames.SetPixel(frame, x, Height - 1 - y, 0, 0, 0);
            }
        }

        return frame;
    }

    // the first border is applied at once; the second identical frame makes the state consistent
    private static void Settle(BlackBorderProcessor processor, Frame frame)
    {
        Assert.True(processor.Process(frame));
        Assert.False(processor.Process(frame));
    }

    private static int FramesUntilChange(BlackBorderProcessor processor, Frame frame, int limit = 1000)
    {
        for (var i = 1; i <= limit; i++)
        {
            if (processor.Process(frame))
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void FirstDetectedBorderIsAppliedAtOnceWithBlur()
    {
        var processor = new BlackBorderProcessor(BlackBorderOptions.Default);

        Assert.True(processor.CurrentBorder.IsUnknown);
        Assert.True(processor.Process(s_letterbox6));
        Assert.Equal(new BlackBorder(false, 7, 0), processor.CurrentBorder);
        Assert.False(processor.Process(s_letterbox6));
    }

    [Fact]
    public void ChangedBorderNeedsBorderFrameCountConsistentFrames()
    {
        var processor = new BlackBorderProcessor(BlackBorderOptions.Default with { BorderFrameCount = 50, MaxInconsistentCount = 10 });
        Settle(processor, s_letterbox6);

        // 11 frames to switch the candidate (10 tolerated), then 50 consistent ones
        Assert.Equal(11 + 50, FramesUntilChange(processor, s_letterbox3));
        Assert.Equal(new BlackBorder(false, 4, 0), processor.CurrentBorder);
    }

    [Fact]
    public void ShortGlitchesAreIgnored()
    {
        var processor = new BlackBorderProcessor(BlackBorderOptions.Default);
        Settle(processor, s_letterbox6);

        for (var round = 0; round < 20; round++)
        {
            for (var i = 0; i < 10; i++)
            {
                Assert.False(processor.Process(s_letterbox3));
            }

            Assert.False(processor.Process(s_letterbox6));
        }

        Assert.Equal(new BlackBorder(false, 7, 0), processor.CurrentBorder);
    }

    [Fact]
    public void UnknownNeedsUnknownFrameCountConsistentFrames()
    {
        var processor = new BlackBorderProcessor(BlackBorderOptions.Default with { UnknownFrameCount = 30 });
        Settle(processor, s_letterbox6);

        Assert.Equal(11 + 30, FramesUntilChange(processor, s_black));
        Assert.True(processor.CurrentBorder.IsUnknown);
    }

    [Fact]
    public void Options_FromJsonReadsTheSetting()
    {
        var options = BlackBorderOptions.FromJson(JsonNode.Parse("""
            { "blurRemoveCnt": 2, "borderFrameCnt": 40, "enable": true, "maxInconsistentCnt": 5, "mode": "letterbox",
              "threshold": 8, "unknownFrameCnt": 300 }
            """));

        Assert.Equal(new BlackBorderOptions(8, 300, 40, 5, 2, BlackBorderMode.Letterbox), options);
        Assert.Equal(BlackBorderOptions.Default, BlackBorderOptions.FromJson(JsonNode.Parse("{}")));
        Assert.Equal(21, new BlackBorderDetector(options.ThresholdPercent / 100).Threshold);
    }

    [Fact]
    public void BlackBorder_UnknownBordersAreEqual()
    {
        Assert.Equal(BlackBorder.Unknown, new BlackBorder(true, 3, 4));
        Assert.NotEqual(BlackBorder.Unknown, BlackBorder.None);
        Assert.NotEqual(new BlackBorder(false, 1, 2), new BlackBorder(false, 2, 1));
    }

    [Fact]
    public void Processing_DoesNotAllocatePerFrame()
    {
        var processor = new BlackBorderProcessor(BlackBorderOptions.Default);
        void Frames()
        {
            for (var i = 0; i < 100; i++)
            {
                processor.Process((i % 3 == 0) ? s_letterbox3 : s_letterbox6);
            }
        }

        // the warm-up goes past tiered-JIT promotion, so recompilation cannot allocate inside the measured window
        Assert.Equal(0, Allocations.Measure(Frames, Frames));
    }
}
