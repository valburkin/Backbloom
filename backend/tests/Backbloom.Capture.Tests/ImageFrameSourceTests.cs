using System.Text;
using Backbloom.Core.Imaging;

namespace Backbloom.Capture.Tests;

public sealed class ImageFrameSourceTests
{
    private static byte[] Ppm(int width, int height, int maxValue, byte[] rgb, string comment = "")
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{comment}{width} {height}\n{maxValue}\n");
        return [.. header, .. rgb];
    }

    [Fact]
    public void SmallPicture_IsUsedAsIs_InBgraOrder()
    {
        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30];
        using var source = new ImageFrameSource(Ppm(2, 2, 255, rgb, "# made by a test\n"), "test.ppm", 60);
        Assert.Equal((2, 2), source.FrameSize);
        Assert.Equal("test.ppm 2x2 @ 60 fps", source.Device);

        var frame = new Frame(2, 2);
        source.Render(frame);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 30, 20, 10, 255 }, frame.ReadOnlyPixels.ToArray());
    }

    [Fact]
    public void WidePicture_IsAveragedDownToTheMaximumWidth()
    {
        // 1030 px wide needs a factor of 3 to fit in 512 px (343 px); 3 rows become one.
        const int Width = 1030, Height = 3;
        var rgb = new byte[Width * Height * 3];
        for (var i = 0; i < rgb.Length; i += 3)
        {
            var x = (i / 3) % Width;
            rgb[i] = (byte)(x % 3 == 0 ? 90 : 0); // red only in every third column: the block average is 30
        }

        using var source = new ImageFrameSource(Ppm(Width, Height, 255, rgb), "wide.ppm", 30);
        Assert.Equal((343, 1), source.FrameSize);
        var frame = new Frame(343, 1);
        source.Render(frame);
        Assert.Equal(30, frame.ReadOnlyPixels[2]);
        Assert.Equal(0, frame.ReadOnlyPixels[1]);
    }

    [Fact]
    public void SmallerMaximumValues_AreScaledTo255()
    {
        using var source = new ImageFrameSource(Ppm(1, 1, 15, [15, 0, 15]), "4bit.ppm", 60);
        var frame = new Frame(1, 1);
        source.Render(frame);
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, frame.ReadOnlyPixels.ToArray());
    }

    [Theory]
    [InlineData("P3\n1 1\n255\n0 0 0")]
    [InlineData("P6\n1 1\n65535\n")]
    [InlineData("P6\n2 2\n255\n\u0001\u0002")]
    [InlineData("P6\n2")]
    public void InvalidPictures_AreRejected(string file)
    {
        Assert.Throws<InvalidDataException>(() => new ImageFrameSource(Encoding.Latin1.GetBytes(file), "bad.ppm", 60));
    }

    [Fact]
    public void CommentsBetweenHeaderValues_AreSkipped()
    {
        var file = Encoding.ASCII.GetBytes("P6 # size\n1 # width\n1\n# max\n255\n");
        using var source = new ImageFrameSource([.. file, 1, 2, 3], "c.ppm", 60);
        var frame = new Frame(1, 1);
        source.Render(frame);
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, frame.ReadOnlyPixels.ToArray());
    }

    [Fact]
    public void PictureShorterThanItsScaleFactor_IsRejected()
    {
        // 1030 px wide needs a factor of 3, but there are only 2 rows to average.
        var error = Assert.Throws<InvalidDataException>(() => new ImageFrameSource(Ppm(1030, 2, 255, new byte[1030 * 2 * 3]), "short.ppm", 60));
        Assert.Contains("at least 3 pixels tall", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesAboveTheMaximum_CountAsTheMaximum()
    {
        using var source = new ImageFrameSource(Ppm(1, 1, 15, [200, 0, 15]), "over.ppm", 60);
        var frame = new Frame(1, 1);
        source.Render(frame);
        Assert.Equal(255, frame.ReadOnlyPixels[2]);
    }

    [Fact]
    public void Picture_IsReadFromAFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backbloom-test-{Guid.NewGuid():N}.ppm");
        File.WriteAllBytes(path, Ppm(2, 1, 255, [255, 0, 0, 0, 0, 255]));
        try
        {
            using var source = new ImageFrameSource(path, 25);
            Assert.Equal((2, 1), source.FrameSize);
            Assert.StartsWith(Path.GetFileName(path), source.Device, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Render_DoesNotAllocate()
    {
        using var source = new ImageFrameSource(Ppm(4, 2, 255, new byte[4 * 2 * 3]), "x.ppm", 60);
        var frame = new Frame(4, 2);
        source.Render(frame);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            source.Render(frame);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
