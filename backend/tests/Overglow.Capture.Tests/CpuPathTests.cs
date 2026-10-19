using System.Text.Json.Nodes;
using Overglow.Capture.Hdr;
using Overglow.Core.Imaging;

namespace Overglow.Capture.Tests;

public sealed class CpuPathTests
{
    [Theory]
    [InlineData(3840, 2160, 512, 8, 480, 270)]
    [InlineData(2560, 1440, 512, 5, 512, 288)]
    [InlineData(1920, 1080, 512, 4, 480, 270)]
    [InlineData(512, 288, 512, 1, 512, 288)]
    [InlineData(1280, 720, 1280, 1, 1280, 720)]
    public void Region_UsesTheSmallestWholeFactorThatFits(int width, int height, int maxWidth, int divide, int targetWidth, int targetHeight)
    {
        var region = CaptureRegion.Compute(width, height, default, maxWidth);
        Assert.Equal(new CaptureRegion(0, 0, width, height, divide, targetWidth, targetHeight), region);
    }

    [Fact]
    public void Region_CropsInScreenPixels()
    {
        var region = CaptureRegion.Compute(3840, 2160, new CropMargins(100, 60, 40, 20), 512);
        Assert.Equal(new CaptureRegion(100, 40, 3680, 2100, 8, 460, 262), region);
    }

    [Fact]
    public void Region_IgnoresCropsThatLeaveTooLittle()
    {
        var region = CaptureRegion.Compute(1920, 1080, new CropMargins(1000, 910, 0, 0), 512);
        Assert.Equal(new CaptureRegion(0, 0, 1920, 1080, 4, 480, 270), region);
    }

    [Fact]
    public void Downscale_TakesEveryDivideThPixelOfTheCroppedArea()
    {
        // 8x4 source with padded rows; pixel (x, y) = B x, G y, R 100, A 0.
        const int Width = 8, Height = 4, Pitch = 40;
        var source = new byte[Pitch * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                source[(y * Pitch) + (x * 4)] = (byte)x;
                source[(y * Pitch) + (x * 4) + 1] = (byte)y;
                source[(y * Pitch) + (x * 4) + 2] = 100;
            }
        }

        var region = new CaptureRegion(X: 2, Y: 0, Width: 6, Height: 4, Divide: 2, TargetWidth: 3, TargetHeight: 2);
        var frame = new Frame(4, 2);

        BgraDownscaler.Downscale(source, Pitch, Height, region, null, frame, targetX: 1);

        var p = frame.ReadOnlyPixels;
        Assert.Equal([0, 0, 0, 0], p[..4].ToArray());
        Assert.Equal([2, 0, 100, 255], p.Slice(4, 4).ToArray());
        Assert.Equal([4, 0, 100, 255], p.Slice(8, 4).ToArray());
        Assert.Equal([6, 0, 100, 255], p.Slice(12, 4).ToArray());
        Assert.Equal([6, 2, 100, 255], p.Slice(frame.Stride + 12, 4).ToArray());
    }

    [Fact]
    public void Downscale_MapsColorsThroughTheLut_AndDoesNotAllocate()
    {
        // A table that swaps red and blue and inverts green.
        var table = new byte[ToneMappingLut.TableSize];
        for (var b = 0; b < 256; b++)
        {
            for (var g = 0; g < 256; g++)
            {
                for (var r = 0; r < 256; r++)
                {
                    var i = ToneMappingLut.Index(r, g, b);
                    (table[i], table[i + 1], table[i + 2]) = ((byte)b, (byte)(255 - g), (byte)r);
                }
            }
        }

        var lut = new ToneMappingLut(table, "test");
        var source = new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 };
        var frame = new Frame(2, 1);
        var region = CaptureRegion.Compute(2, 1, default, 2);

        BgraDownscaler.Downscale(source, 8, 1, region, lut, frame);
        Assert.Equal([30, 235, 10, 255, 60, 205, 40, 255], frame.ReadOnlyPixels.ToArray());

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            BgraDownscaler.Downscale(source, 8, 1, region, lut, frame);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(3, 0, new[] { 0, 1, 2 })]
    [InlineData(3, 1, new[] { 0, 2, 1 })]
    [InlineData(3, 2, new[] { 1, 0, 2 })]
    [InlineData(3, 5, new[] { 2, 1, 0 })]
    [InlineData(3, 6, new[] { 0, 1, 2 })]
    [InlineData(3, 200, new[] { 0, 1, 2 })]
    [InlineData(2, 1, new[] { 1, 0 })]
    public void DisplayOrder_FollowsStdNextPermutation(int count, int permutation, int[] expected)
    {
        Assert.Equal(expected, DisplayOrder.Permute(count, permutation));
    }

    [Fact]
    public void Settings_ReadHyperHdrNames_WithDefaultsForMissingValues()
    {
        var owner = JsonNode.Parse("""
            {"cropBottom":4,"cropLeft":2,"cropRight":0,"cropTop":0,"device":"\\\\.\\DISPLAY5|NVIDIA GeForce RTX 5080","fps":30,
             "hardware":true,"hdrToneMapping":true,"monitor_nits":200,"reorder_displays":0,"videoMode":512}
            """);
        var settings = CaptureSettings.FromJson(owner);
        Assert.Equal(@"\\.\DISPLAY5|NVIDIA GeForce RTX 5080", settings.Device);
        Assert.Equal((512, 30, true, true, 200), (settings.MaxWidth, settings.FramesPerSecond, settings.HdrToneMapping, settings.Hardware, settings.MonitorNits));
        Assert.Equal(new CropMargins(2, 0, 0, 4), settings.Crop);

        Assert.Equal(new CaptureSettings(), CaptureSettings.FromJson(null));
        Assert.Equal("systemGrabber", CaptureSettings.SectionName);
    }
}
