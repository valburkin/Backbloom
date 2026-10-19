using Overglow.Capture;
using Overglow.Core.Imaging;

namespace Overglow.Engine.Tests;

public sealed class SyntheticFrameSourceTests
{
    [Fact]
    public void Render_ScrollsTheRainbowOverTime()
    {
        using var source = new SyntheticFrameSource(new SyntheticFrameSourceOptions(Width: 64, Height: 36, CycleSeconds: 8));
        var a = new Frame(64, 36);
        var b = new Frame(64, 36);

        source.Render(a, 0);
        source.Render(b, 2);

        Assert.False(a.ReadOnlyPixels.SequenceEqual(b.ReadOnlyPixels));
        // At t = 0 the top-left pixel is pure red (hue 0), fully opaque, in BGRA order.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, a.ReadOnlyPixels[..4].ToArray());

        // One full cycle later the picture repeats.
        source.Render(b, 8);
        Assert.True(a.ReadOnlyPixels.SequenceEqual(b.ReadOnlyPixels));
    }

    [Fact]
    public void TryCapture_StopsWhenCanceled()
    {
        using var source = new SyntheticFrameSource(new SyntheticFrameSourceOptions(FramesPerSecond: 1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(source.TryCapture(new Frame(512, 288), cts.Token));
    }
}
