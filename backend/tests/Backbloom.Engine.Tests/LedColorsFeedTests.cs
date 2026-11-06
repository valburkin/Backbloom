using System.Numerics;

namespace Backbloom.Engine.Tests;

public sealed class LedColorsFeedTests
{
    [Fact]
    public void Feed_KeepsEachFramesShareWithItsColors()
    {
        var feed = new LedColorsFeed();
        var preview = new byte[3];

        feed.Publish([new Vector3(1, 0.5f, 0)], 0.25f);
        Assert.Equal(3, feed.CopyLatest(preview, out var share));
        Assert.Equal(0.25f, share);
        Assert.Equal(255, preview[0]);

        // a frame without a share is at full output; odd values are clamped, NaN counts as full
        feed.Publish([new Vector3(1, 0, 0)]);
        feed.CopyLatest(preview, out share);
        Assert.Equal(1f, share);
        feed.Publish([new Vector3(1, 0, 0)], 3f);
        feed.CopyLatest(preview, out share);
        Assert.Equal(1f, share);
        feed.Publish([new Vector3(1, 0, 0)], -1f);
        feed.CopyLatest(preview, out share);
        Assert.Equal(0f, share);
        feed.Publish([new Vector3(1, 0, 0)], float.NaN);
        feed.CopyLatest(preview, out share);
        Assert.Equal(1f, share);
    }
}
